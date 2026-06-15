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
    /// The tool allow-list each spawned agent is restricted to (P2-3) — the supervisor config's REUSED
    /// <c>AllowedTools</c> threaded into <c>AgentTask.Tools</c>. Tri-state, matching the task envelope: <c>null</c>
    /// (the default, and pre-P2-3) = the harness default; non-empty = exactly these (UNIONed with a persona's tools
    /// by the dispatch-time resolver). Carried separately from <see cref="AgentProfile"/> because it reuses the
    /// existing <c>SupervisorGoalConfig.AllowedTools</c> field rather than duplicating it on the profile.
    /// </summary>
    public IReadOnlyList<string>? SpawnedAgentTools { get; init; }
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
