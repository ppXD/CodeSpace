using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The folded state of a supervisor run AT THE START OF A TURN — the replay of every prior decision from
/// the durable <c>SupervisorDecisionRecord</c> ledger (a data noun, Rule 18.1: only primitives, never the
/// Core entity). Built by <c>RehydrateFromDecisionLog</c> by reading the ledger in <c>Sequence</c> order:
/// a TERMINAL decision is replayed into <see cref="PriorDecisions"/> (its outcome only — the side effect is
/// NOT re-run), and the one in-flight decision (a non-terminal row, if any) becomes <see cref="InFlight"/>
/// — the resume target the turn re-claims rather than re-emits.
///
/// <para>The decider reads this to choose the next decision deterministically (E2 stub: turn 1 = plan, turn
/// 2 = stop). <see cref="TurnNumber"/> is 0-based and equals <see cref="PriorDecisions"/>.Count +
/// (InFlight is null ? 0 : 0) — i.e. the number of DECIDED turns so far; it drives both the next decision
/// and the per-turn <c>IterationKey</c>. <see cref="Goal"/> is the run-level goal the supervisor pursues.</para>
/// </summary>
public sealed record SupervisorTurnContext
{
    /// <summary>The run-level goal the supervisor is pursuing (carried from node config).</summary>
    public string Goal { get; init; } = "";

    /// <summary>
    /// The supervisor run id (the WorkflowRun id) — the executor links spawned agent runs + their AgentRun waits
    /// to it (E3). Carried so the executor stays a pure-of-engine service (Rule 16). <see cref="Guid.Empty"/> in a
    /// pure-unit context that never spawns.
    /// </summary>
    public Guid SupervisorRunId { get; init; }

    /// <summary>The run's team (tenancy) — spawned agent runs inherit it; never model-supplied. <see cref="Guid.Empty"/> in a pure-unit context.</summary>
    public Guid TeamId { get; init; }

    /// <summary>
    /// The <c>agent.supervisor</c> node id — the prefix of the per-turn-per-spawn AgentRun wait IterationKey
    /// (<c>&lt;nodeId&gt;#turn{N}#{k}</c>, must-fix #1). Empty in a pure-unit context that never spawns.
    /// </summary>
    public string NodeId { get; init; } = "";

    /// <summary>
    /// The conversation an <c>ask_human</c> turn posts its question card into (E4) — carried from node config, the
    /// SAME <c>x-selector: conversation</c> shape agent.code uses for its approval surface. Tenancy is enforced at
    /// post time (the conversation must belong to <see cref="TeamId"/>; never model-supplied). <c>null</c> when the
    /// supervisor was authored without a conversation → an ask_human turn degrades to a clean no-surface stop.
    /// </summary>
    public Guid? ConversationId { get; init; }

    /// <summary>The 0-based number of the turn about to run = how many prior DECIDED (terminal) decisions exist. Drives the next decision + the per-turn IterationKey.</summary>
    public int TurnNumber { get; init; }

    /// <summary>The replayed terminal decisions in <c>Sequence</c> order — outcome only, side effects NOT re-run. The exactly-once replay tape.</summary>
    public IReadOnlyList<SupervisorPriorDecision> PriorDecisions { get; init; } = Array.Empty<SupervisorPriorDecision>();

    /// <summary>The single in-flight (non-terminal) decision, if any — a turn crashed AFTER claiming but BEFORE recording terminal. The resume target the turn re-claims (idempotent), never re-emits. Null on the common path.</summary>
    public SupervisorPriorDecision? InFlight { get; init; }

    /// <summary>
    /// The total number of agents this run has spawned so far, SUMMED from the durable ledger (every prior
    /// <c>spawn</c> / <c>retry</c> decision's recorded <c>agentCount</c>) — a LEDGER FACT folded on rehydrate,
    /// so it survives replay and can't be reset by re-entering the node (PR-E E5 total-spawn cap). The turn loop
    /// fail-closed force-STOPs when a further spawn would push this past the run's <c>MaxTotalSpawns</c>.
    /// </summary>
    public int TotalSpawnedAgents { get; init; }

    /// <summary>
    /// The run's TOTAL REALIZED USD spend so far (SOTA #4 + P3.5) — <see cref="AgentExecutionSpendUsd"/> PLUS
    /// <see cref="BrainPlaneSpendUsd"/>, summed from the durable ledger. A LEDGER FACT folded on rehydrate (like
    /// <see cref="TotalSpawnedAgents"/>), so it survives replay + re-entry deterministically. The turn loop force-STOPs
    /// when this EXCEEDS the run's <c>MaxCostUsd</c> (realized-spend backpressure — exactly-at-budget proceeds, like
    /// the total-spawn cap). 0 when no spend is known yet (fail-open — cost never blocks the first spawn).
    /// </summary>
    public decimal RunSpendUsd { get; init; }

    /// <summary>P3.5 — the SPAWNED-AGENT (coding harness) share of <see cref="RunSpendUsd"/> — every prior spawn/retry decision's folded agent token usage, priced. Broken out from the brain-plane share so the recitation/stop-detail can show a real breakdown, not just one merged figure.</summary>
    public decimal AgentExecutionSpendUsd { get; init; }

    /// <summary>P3.5 — the IN-PROCESS MODEL-CALL share of <see cref="RunSpendUsd"/>: the supervisor's own decision calls, a decision critic's review, a plan-authoring call, an acceptance-grading judge — every <c>interaction.completed</c> ledger row this run recorded, priced. 0 when no cost cap is set (the fold is skipped entirely — zero DB cost for the common uncapped run).</summary>
    public decimal BrainPlaneSpendUsd { get; init; }

    /// <summary>P3.5 — <see cref="BrainPlaneSpendUsd"/> broken out by its open <c>kind</c> label (e.g. <c>"supervisor.decision"</c>, <c>"critic.review"</c>, <c>"grader.acceptance"</c>) — the per-lane figures the budget recitation and the cost-cap stop detail both render. Empty when no cost cap is set.</summary>
    public IReadOnlyDictionary<string, decimal> BrainPlaneSpendByKind { get; init; } = EmptySpendByKind;

    /// <summary>P3.5 — the run's realized-spend cap in USD (carried from <c>SupervisorGoalPlan.MaxCostUsd</c> so the DECIDER can recite it — <c>DecideAsync</c> receives only this context, never the plan). Null = no cost cap; the budget recitation renders nothing.</summary>
    public decimal? MaxCostUsd { get; init; }

    private static readonly IReadOnlyDictionary<string, decimal> EmptySpendByKind = new Dictionary<string, decimal>();

    /// <summary>
    /// How many of the MOST RECENT consecutive decisions produced NO new SETTLED agent result, folded from the
    /// durable ledger (PR-E E5 best-effort no-progress guard). A spawn/retry that staged agents whose outcomes
    /// the merge has not yet folded counts as no-progress; a fresh agent result resets it to 0. The turn loop
    /// force-STOPs at the run's no-progress cap.
    /// </summary>
    public int NoProgressDecisions { get; init; }

    /// <summary>
    /// The approval policy in force for this run (PR-E E5 governance) — carried so the executor / turn loop routes
    /// every side-effecting decision through <c>AgentToolGate</c> at the policy-derived tier. Default
    /// <see cref="Dtos.Agents.SupervisorApprovalPolicy.None"/> matches pre-E5 behaviour (no gate).
    /// </summary>
    public Dtos.Agents.SupervisorApprovalPolicy ApprovalPolicy { get; init; }

    /// <summary>
    /// The default agent profile (P2-3) every spawned agent inherits — repo / harness / model / persona /
    /// credential / runner / MCP / autonomy — carried from the supervisor node's config so the executor's
    /// <c>BuildAgentTask</c> stamps a REAL team agent envelope, not a bare skeleton. <c>null</c> (the default,
    /// and what a pre-P2-3 supervisor resolves to) makes <c>BuildAgentTask</c> produce EXACTLY today's bare
    /// task (codex-cli / Standard / no-repo), so an existing supervisor spawn is byte-identical.
    /// </summary>
    public Dtos.Agents.SupervisorAgentProfile? AgentProfile { get; init; }

    /// <summary>
    /// The operator's OBJECTIVE acceptance floor (carried from <c>SupervisorGoalConfig.AcceptanceChecks</c>, blank
    /// entries dropped) — the argv a terminal verdict is graded against. Enforced on the <c>resolve</c> verdict (L4
    /// A3) AND on every terminal <c>stop</c> (L4 C1), so it gates the run's FINAL reviewable head even on a clean
    /// (no-conflict) run that never resolved — the model can tighten acceptance with its own stop command but can
    /// never ship past this floor. <c>null</c> (none / all-blank) ⇒ no operator grade runs (byte-identical).
    /// </summary>
    public IReadOnlyList<string>? AcceptanceChecks { get; init; }

    /// <summary>The operator's free-text ACCEPTANCE CRITERIA (blank entries dropped) — the definition of done rendered into the decider prompt so the model targets it. NOT executed (distinct from <see cref="AcceptanceChecks"/>). Null/empty (none / all-blank) ⇒ no prompt block (byte-identical). The intended yardstick for a future supervisor critic-gate.</summary>
    public IReadOnlyList<string>? AcceptanceCriteria { get; init; }

    /// <summary>Whether an AUTHORED plan must be confirmed by a human before any agent runs (triad S3 gate) — the turn loop parks an ask_human confirmation card after each unconfirmed plan version. False (the default) ⇒ no gate.</summary>
    public bool RequirePlanConfirmation { get; init; }

    /// <summary>The PLAN-scoped critic (S4e): a <c>plan</c> decision reviews under THIS mode when set (else under <c>DecisionReviewMode</c>); non-plan decisions never use it. None (default) ⇒ byte-identical.</summary>
    public ReviewMode PlanReviewMode { get; init; } = ReviewMode.None;

    /// <summary>D①: PLAN decisions review via a REAL independent agent (grounded against the repo) before the model critic. Carried from <c>SupervisorGoalConfig.ReviewerAgent</c>.</summary>
    public bool ReviewerAgent { get; init; }

    /// <summary>
    /// The tool allow-list each spawned agent is restricted to (P2-3) — the supervisor config's REUSED
    /// <c>AllowedTools</c> threaded into <c>AgentTask.Tools</c>. Tri-state, matching the task envelope: <c>null</c>
    /// (the default, and pre-P2-3) = the harness default; non-empty = exactly these (UNIONed with a persona's tools
    /// by the dispatch-time resolver). Carried separately from <see cref="AgentProfile"/> because it reuses the
    /// existing <c>SupervisorGoalConfig.AllowedTools</c> field rather than duplicating it on the profile.
    /// </summary>
    public IReadOnlyList<string>? SpawnedAgentTools { get; init; }

    /// <summary>
    /// The operator's ALLOWED MODEL POOL for spawned agents (the model analogue of the bound repos) — a list of
    /// credentialed-model ROW ids. Every dispatched agent's effective model must resolve to a row in this pool and runs
    /// on that row's credential; out of pool → fail closed. Null / empty = the pool is ALL the team's credentialed
    /// models. Threaded from <c>SupervisorGoalConfig.AllowedModelIds</c>.
    /// </summary>
    public IReadOnlyList<Guid>? AllowedModelIds { get; init; }

    /// <summary>The operator's ALLOWED AGENT (persona) POOL for spawned agents — a list of <c>AgentDefinition</c> ROW ids. Every dispatched agent's effective persona (model-authored slug OR profile default) must be in this pool, else fail closed. Null / empty = ALL the team's personas. Threaded from <c>SupervisorGoalConfig.AllowedAgentDefinitionIds</c>.</summary>
    public IReadOnlyList<Guid>? AllowedAgentDefinitionIds { get; init; }

    /// <summary>The credentialed-model ROW id the supervisor's own decider runs on (carried from <c>SupervisorGoalConfig.SupervisorModelId</c>). REQUIRED — the decider resolves this row to its model + credential and fails closed when null/unresolvable. Distinct from the agent pool (<see cref="AllowedModelIds"/>): the brain is the operator's explicit pick, never bounded by the agent allow-list.</summary>
    public Guid? SupervisorModelId { get; init; }

    /// <summary>How (if at all) an INDEPENDENT critic reviews each turn's decision before its side effect (carried from <c>SupervisorGoalConfig.DecisionReviewMode</c>). <see cref="ReviewMode.None"/> (the default) ⇒ the critic decorator is a pure passthrough ⇒ byte-identical. <see cref="ReviewMode.Improve"/> ⇒ one bounded re-decide against the critique. Baked durably so every turn + replay reads the same mode.</summary>
    public ReviewMode DecisionReviewMode { get; init; } = ReviewMode.None;

    /// <summary>The credentialed-model ROW id the decision critic runs on (carried from <c>SupervisorGoalConfig.ReviewerModelId</c>). Null ⇒ the critic auto-picks the team's strongest structured-eligible brain. Only consulted when <see cref="DecisionReviewMode"/> is not <see cref="ReviewMode.None"/>.</summary>
    public Guid? ReviewerModelId { get; init; }

    /// <summary>TRANSIENT (never baked): the critic's critique folded in for ONE bounded re-decide. Set ONLY by the critic decorator's second <c>DecideAsync</c> call (with <see cref="DecisionReviewMode"/> reset to None to prevent recursion); the decider renders it into its prompt so the model revises against it. Null on the first decide.</summary>
    public string? ReviewerCritique { get; init; }

    /// <summary>
    /// The run's rolling TAPE SUMMARY (P1.2 auto-compact) — a persisted model-written digest of the decisions with
    /// <c>Sequence ≤ UpToSequence</c>, loaded at rehydrate. PROMPT-GRAIN ONLY: <see cref="PriorDecisions"/> stays the
    /// COMPLETE tape (bounds, recitation, and replay all read the full list); only the decider's prompt substitutes
    /// the summarized head for the raw rows, so a long run's prompt stops growing without ever changing a bound.
    /// Null = nothing compacted yet (byte-identical prompt).
    /// </summary>
    public SupervisorTapeSummary? TapeSummary { get; init; }

    /// <summary>
    /// The PENDING decisions this run's CHILD agent runs raised and are blocked on (Decision substrate D4c-2), read off
    /// the cross-grain queue on rehydrate (soonest-deadline first). The arbiter drains these BEFORE the decider each turn:
    /// it auto-answers the ones it is confident about (low/med risk, within the fail-closed floor) and LEAVES the rest in
    /// the queue for a human. Empty (the common no-pending-child path) keeps the turn byte-identical to pre-D4c-2 + DB-free.
    /// A pure data noun (Rule 18.1) — the projected <see cref="PendingDecision"/>, never the Core entity.
    /// </summary>
    public IReadOnlyList<PendingDecision> PendingChildDecisions { get; init; } = Array.Empty<PendingDecision>();
}

/// <summary>One prior decision replayed from the ledger — its row id + kind + emitted payload + (for a terminal) its recorded outcome. A pure data noun.</summary>
public sealed record SupervisorPriorDecision
{
    /// <summary>The ledger row id — the in-flight decision's claim identity, so a frozen replay re-executes UNDER the existing claim (no key re-derivation, which jsonb whitespace-normalization would otherwise break).</summary>
    public required Guid Id { get; init; }
    public required long Sequence { get; init; }
    public required string DecisionKind { get; init; }
    public required SupervisorDecisionStatus Status { get; init; }
    public required string PayloadJson { get; init; }
    public string? OutcomeJson { get; init; }
    public string? Error { get; init; }
}
