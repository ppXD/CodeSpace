namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// The operator's GOAL-not-GRAPH surface for an <c>agent.supervisor</c> node (PR-E E5, Rule 18.1 — a data noun
/// parsed from the node's raw <c>Config</c> JSON). The operator authors a GOAL plus a few SAFETY LIMITS; the
/// supervisor generates the work (plan → spawn → … ). Everything here is OPTIONAL and parsed LENIENTLY into a
/// <c>SupervisorGoalPlan</c> (mirrors <c>MapConfig</c> → <c>MapPlan</c>): a null / blank / out-of-range field
/// falls back to a safe default, so an existing flag-on supervisor authored before E5 (goal only) behaves
/// exactly as before — the bounds resolve to the <c>SupervisorLane</c> consts.
///
/// <para>The limits are the FAIL-CLOSED bounds the turn loop enforces, each counted from the DURABLE ledger so
/// it survives replay + can't be reset by re-entering the node. Hitting any one FORCE-STOPS the run cleanly
/// with a distinct terminal reason — never a silent truncation, never an unbounded run.</para>
/// </summary>
public sealed record SupervisorGoalConfig
{
    /// <summary>The objective the supervisor pursues across its turns — folded into the LLM decider's prompt. The single thing an operator must author.</summary>
    public string? Goal { get; init; }

    /// <summary>Optional allow-list of harness / agent kinds the supervisor may spawn (e.g. <c>["codex-cli"]</c>). Null / empty = no restriction (the harness default). RESERVED — stored + parsed; the spawn-time enforcement gate is a follow-up.</summary>
    public IReadOnlyList<string>? AllowedAgents { get; init; }

    /// <summary>Optional allow-list of tool kinds spawned agents may use. RESERVED — stored only; threaded into the spawned <c>AgentTask.Tools</c> in a follow-up.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Optional cap on how many agents one spawn decision may fan out at once. Null = the schema's hard <c>maxItems</c> (20). Clamped to <c>[1, 20]</c> by the plan.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>Optional cap on how many DECISIONS (turns) the supervisor may take before a fail-closed force-STOP. Null = the <c>SupervisorLane.DecisionBudget</c> default (30). Clamped to <c>[1, DecisionBudget]</c> — an operator may TIGHTEN the budget, never raise it past the hard ceiling.</summary>
    public int? MaxRounds { get; init; }

    /// <summary>Optional cap on how many agents the supervisor may spawn IN TOTAL across the whole run (summed from the ledger). Null = the <c>SupervisorLane.DefaultMaxTotalSpawns</c> default. Clamped to <c>[1, MaxTotalSpawnsCeiling]</c>.</summary>
    public int? MaxTotalSpawns { get; init; }

    /// <summary>Optional cap on consecutive decisions producing no new settled agent result before the best-effort no-progress guard force-STOPs. Null = the <c>SupervisorLane.DefaultMaxNoProgressDecisions</c> default.</summary>
    public int? MaxNoProgressDecisions { get; init; }

    /// <summary>Optional acceptance checks the operator wants verified before the supervisor declares success. RESERVED — stored + parsed; the enforcing acceptance gate is a follow-up.</summary>
    public IReadOnlyList<string>? AcceptanceChecks { get; init; }

    /// <summary>
    /// Which decisions require a human in the loop before their side effect fires (PR-E E5 governance). Parsed
    /// leniently into <see cref="SupervisorApprovalPolicy"/>: <c>"none"</c> (autonomous), <c>"spawns"</c> /
    /// <c>"side-effects"</c> (a human approves every spawn/retry before any agent is created). Unknown / blank =
    /// the safe default <see cref="SupervisorApprovalPolicy.None"/> (matches pre-E5 behaviour — no gate). The
    /// policy maps to the autonomy tier <c>AgentToolGate</c> reads.
    /// </summary>
    public string? ApprovalPolicy { get; init; }
}

/// <summary>
/// Which supervisor decisions require a human approval before their side effect runs (PR-E E5). The supervisor's
/// side-effecting decisions are <c>spawn</c> / <c>retry</c> (they CREATE agent runs); <c>plan</c> / <c>merge</c>
/// / <c>stop</c> are read-only / terminal and never gated. The policy maps to the autonomy tier
/// <c>AgentToolGate</c> consults, so the SAME governance gate the MCP tool fabric uses decides whether a spawn
/// proceeds, parks for approval, or is refused.
/// </summary>
public enum SupervisorApprovalPolicy
{
    /// <summary>No approval required — the supervisor spawns autonomously (maps to the Unleashed tier; pre-E5 behaviour, the safe parse default).</summary>
    None,

    /// <summary>Every spawn / retry requires a human approval before any agent is created (maps to a tier that RequireApproval-gates a side-effecting decision). The supervisor parks on the approval card, the human's answer resumes it.</summary>
    Spawns,
}
