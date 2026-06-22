using System.Text.Json;

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

    /// <summary>Optional allow-list of tool kinds spawned agents may use — threaded into each spawned <c>AgentTask.Tools</c> (via <c>SupervisorTurnContext.SpawnedAgentTools</c>). Null / empty = no restriction.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Optional cap on how many agents one spawn decision may fan out at once. Null = the schema's hard <c>maxItems</c> (20). Clamped to <c>[1, 20]</c> by the plan.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>Optional cap on how many DECISIONS (turns) the supervisor may take before a fail-closed force-STOP. Null = the <c>SupervisorLane.DecisionBudget</c> default (30). Clamped to <c>[1, DecisionBudget]</c> — an operator may TIGHTEN the budget, never raise it past the hard ceiling.</summary>
    public int? MaxRounds { get; init; }

    /// <summary>Optional cap on how many agents the supervisor may spawn IN TOTAL across the whole run (summed from the ledger). Null = the <c>SupervisorLane.DefaultMaxTotalSpawns</c> default. Clamped to <c>[1, MaxTotalSpawnsCeiling]</c>.</summary>
    public int? MaxTotalSpawns { get; init; }

    /// <summary>Optional cap on the run's REALIZED USD spend (summed priced agent token usage, SOTA #4). Null = no cost cap (the agent-count cap still bounds the run). A negative value resolves to null (no budget, not a zero budget). At/below the cap proceeds; spend ABOVE it force-STOPs the next spend-incurring decision.</summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>Optional cap on consecutive decisions producing no new settled agent result before the best-effort no-progress guard force-STOPs. Null = the <c>SupervisorLane.DefaultMaxNoProgressDecisions</c> default.</summary>
    public int? MaxNoProgressDecisions { get; init; }

    /// <summary>Optional cap on how many <c>resolve</c> attempts the supervisor may make against a conflicted integration (resolver loop #379). Null = the <c>SupervisorLane.DefaultMaxResolveAttempts</c> default (1). Clamped to <c>[1, MaxResolveAttemptsCeiling]</c>.</summary>
    public int? MaxResolveAttempts { get; init; }

    /// <summary>Optional acceptance checks the operator wants verified before the supervisor declares success — the OPERATOR FLOOR (L4 P1), an argv (e.g. <c>["sh","check.sh"]</c>). ENFORCED at the terminal <c>stop</c>: the run's reviewable head is cloned and graded against this command (mandatory when set), and a non-zero exit fails the stop (status <c>AcceptanceFailed</c>) + WITHHOLDS the reviewable branch — so the supervisor can't declare success on an unverified head. Null / empty = no floor.</summary>
    public IReadOnlyList<string>? AcceptanceChecks { get; init; }

    /// <summary>
    /// Which decisions require a human in the loop before their side effect fires (PR-E E5 governance). Parsed
    /// leniently into <see cref="SupervisorApprovalPolicy"/>: <c>"none"</c> (autonomous), <c>"spawns"</c> /
    /// <c>"side-effects"</c> (a human approves every spawn/retry before any agent is created). Unknown / blank =
    /// the safe default <see cref="SupervisorApprovalPolicy.None"/> (matches pre-E5 behaviour — no gate). The
    /// policy maps to the autonomy tier <c>AgentToolGate</c> reads.
    /// </summary>
    public string? ApprovalPolicy { get; init; }

    /// <summary>
    /// The DEFAULT agent profile every agent this supervisor spawns inherits (P2-3) — the supervisor's
    /// analogue of the <c>agent.code</c> node's config: repo / harness / model / persona / credential / runner /
    /// MCP / autonomy. Wholly OPTIONAL: when absent (or all-null), a spawned agent is the bare
    /// <c>codex-cli</c> / Standard / no-repo task that pre-P2-3 supervisors produced, so an existing flag-on
    /// supervisor authored before this field is byte-identical. <see cref="AllowedTools"/> stays the tool
    /// allow-list (it is threaded into <c>AgentTask.Tools</c> here); this groups the rest so the goal / bounds /
    /// policy concerns stay separate (Rule 18.1 — a nested data noun).
    /// </summary>
    public SupervisorAgentProfile? AgentProfile { get; init; }

    /// <summary>
    /// The credentialed-model ROW (a <c>ModelCredentialModel</c> id) the SUPERVISOR's own decider runs on — the "brain",
    /// distinct from the agents it spawns. REQUIRED whenever the supervisor runs: the decider resolves this exact row to
    /// its model + backing credential (it must be a team-owned, enabled, structured-capable row), so the brain is never
    /// guessed and never hardcoded. A row id (not a name) is unambiguous — the same model id under two credentials picks
    /// the right key. Null → the decider fails closed (the UI may recommend a default, but the input must be present).
    /// </summary>
    public Guid? SupervisorModelId { get; init; }

    /// <summary>
    /// The operator's ALLOWED MODEL POOL for the agents this supervisor dispatches — a multi-select of credentialed-model
    /// ROW ids (<c>ModelCredentialModel</c> ids), the model analogue of the bound repos. Every dispatched agent's
    /// effective model (model-authored, profile default, OR persona) must resolve to a row in this pool, and runs on
    /// THAT row's credential; out of pool → FAILS CLOSED. Row ids (not names) are unambiguous — the same model id under
    /// two credentials picks the right key. Null / empty = the pool is ALL the team's credentialed models (every
    /// dispatched model must still be a credentialed row — just not narrowed to a subset).
    /// </summary>
    public IReadOnlyList<Guid>? AllowedModelIds { get; init; }
}

/// <summary>
/// The default envelope a supervisor stamps on every agent it spawns (P2-3, Rule 18.1 — a pure data noun in
/// Messages). Mirrors the <c>agent.code</c> node's config→<c>AgentTask</c> mapping so a supervisor-spawned
/// agent is a REAL team agent (persona-merged, repo-cloned, MCP-capable), not a bare skeleton. Every field is
/// OPTIONAL and folds to the SAME default <c>agent.code</c> uses: a null harness → <c>codex-cli</c>, a null
/// autonomy → <c>Standard</c>, a null repo → analysis-only, a null persona → a pure-inline run. The spawned
/// task's GOAL is the supervisor's per-subtask instruction (never authored here); a persona's system prompt is
/// merged onto it by the dispatch-time resolver, exactly as for <c>agent.code</c>.
/// </summary>
public sealed record SupervisorAgentProfile
{
    /// <summary>The repository each spawned agent clones into its workspace (the executor clones it). Null → no workspace (analysis-only). The supervisor node has no inputs, so this is authored on the profile, not bound from a trigger. The PRIMARY repo when <see cref="RelatedRepositories"/> are also authored.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>
    /// Multi-repo (resolver loop #379, S7): the authored <c>relatedRepositories</c> array — each
    /// <c>{repositoryId, alias?, access?}</c> — that each spawned agent ALSO clones alongside the primary
    /// <see cref="RepositoryId"/>, for a coordinated change across e.g. a frontend + backend. Captured as the RAW
    /// element (not a typed list) so the executor parses it through the SHARED <c>AgentWorkspaceAuthoring</c> the
    /// agent.code node uses — ONE authored-repos → workspace projection, no per-producer mirror (Rule 7), and no
    /// brittle enum-deserialization of <c>access</c>. Null / absent / empty → a single-repo spawn (byte-identical).
    /// </summary>
    public JsonElement? RelatedRepositories { get; init; }

    /// <summary>The harness each spawned agent runs on (e.g. <c>"codex-cli"</c>). Null / blank → the supervisor's <c>codex-cli</c> default — byte-identical to pre-P2-3.</summary>
    public string? Harness { get; init; }

    /// <summary>The model id within the harness's catalog. Null / blank → the persona's model → the harness default (the model-empty rule).</summary>
    public string? Model { get; init; }

    /// <summary>The Agent persona (<c>AgentDefinition</c>) each spawned agent embodies. Null → a pure-inline run. When set, the dispatch-time resolver merges its system prompt + model + tools + credential into the task.</summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>The <c>ModelCredential</c> reference each spawned agent authenticates with (decrypted just-in-time). Null → the persona default → the team/operator fallback.</summary>
    public Guid? ModelCredentialId { get; init; }

    /// <summary>The sandbox runner each spawned agent executes on (e.g. <c>"local"</c>). Null → the executor's default.</summary>
    public string? RunnerKind { get; init; }

    /// <summary>Per-run opt-in to the MCP tool-fabric endpoint for each spawned agent. Null → defer to the ambient deployment flag (an ordinary spawn is unchanged).</summary>
    public bool? EnableMcp { get; init; }

    /// <summary>Per-run opt-in to publishing each spawned agent's diff as its own branch (the one-agent-one-branch fan-out, so the supervisor's per-turn spawns each land on their own branch). Null → defer to the ambient deployment push flag (an ordinary spawn is unchanged).</summary>
    public bool? PushBranch { get; init; }

    /// <summary>Per-run opt-in to INTEGRATING the spawned agents' diffs into one reviewable branch at <c>merge</c> time (SOTA #3 — integrate, not narrate), plus a model synthesis over their diffs. Null / false → defer to the ambient integrate flag; the merge produces only the deterministic side-by-side fold (byte-identical to pre-SOTA-#3). Requires <see cref="RepositoryId"/> for the on-disk integration half.</summary>
    public bool? IntegrateBranches { get; init; }

    /// <summary>The autonomy tier each spawned agent runs at, parsed case-insensitively. Null / unrecognised → the safe <c>Standard</c> default (workspace write, no network) — byte-identical to pre-P2-3.</summary>
    public string? AutonomyLevel { get; init; }
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
