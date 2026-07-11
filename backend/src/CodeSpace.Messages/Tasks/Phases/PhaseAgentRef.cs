namespace CodeSpace.Messages.Tasks.Phases;

/// <summary>
/// A reference to ONE agent run a phase fanned out to — a map branch's agent, a supervisor spawn's child, or the
/// single agent of an agent.run node. The UI links/embeds the run's live timeline from <see cref="AgentRunId"/>.
/// <see cref="Status"/> is the GROUND-TRUTH <c>AgentRunStatus</c> NAME for BOTH the structural node source and the
/// supervisor source (read team-scoped from the <c>AgentRun</c> row, never the structural <c>NodeStatus</c> /
/// decider self-report) — open on the wire, so a new status never breaks the renderer. <see cref="Label"/> is an
/// optional cheap display label (the harness today; a richer per-agent goal is a later FE-PR concern, deliberately
/// not deserialized here).
/// </summary>
public sealed record PhaseAgentRef
{
    public required Guid AgentRunId { get; init; }

    /// <summary>The agent.run node id (or the supervisor node id) the run links back to. Null for a standalone ref.</summary>
    public string? NodeId { get; init; }

    /// <summary>The branch / turn iteration key this agent ran under (e.g. <c>map#0</c>, <c>sup#turn1#0</c>). Null for a non-iterated agent.</summary>
    public string? IterationKey { get; init; }

    /// <summary>The agent run's GROUND-TRUTH status as the <c>AgentRunStatus</c> enum NAME (open string — Queued/Running/Succeeded/Failed/Cancelled/TimedOut). Both sources read it team-scoped from the real AgentRun row; the node source falls back to the owning node's status name only when the agent row is absent.</summary>
    public required string Status { get; init; }

    /// <summary>An optional cheap display label (the harness kind today).</summary>
    public string? Label { get; init; }

    /// <summary>The model-authored semantic ROLE this agent runs in (e.g. "backend implementer", "security reviewer"), off the spawn's per-agent dispatch spec — so the fan-out reads as a division of labour, not anonymous clones. Null for a homogeneous spawn (no per-agent role) or a non-supervisor agent. Open string — display only, never switched on.</summary>
    public string? Role { get; init; }

    /// <summary>The TITLE of the planned subtask this agent was assigned (the model's decomposition), joined from the plan's subtasks through the spawn's <c>subtaskIds[i]</c> ↔ <c>agentRunIds[i]</c> staging order. Null when the agent isn't a supervisor spawn or the plan carried no matching subtask. Display only.</summary>
    public string? AssignedSubtask { get; init; }

    /// <summary>A concise one-line title derived from a plain node / map agent's GOAL (its instruction) — so a fan-out branch reads as its subtask instead of a structural <c>map#N</c> key, unifying the display name with the supervisor source's <see cref="Role"/>. Null for a supervisor spawn (which carries <see cref="Role"/>/<see cref="AssignedSubtask"/>) or when the agent's goal is empty. Display only.</summary>
    public string? Goal { get; init; }

    /// <summary>The model the agent ran on (e.g. a pinned <c>claude-*</c>), or null when unpinned/unknown. Populated for SUPERVISOR-spawned agents (off the folded <c>agentResults</c> compact); null for a plain node/map agent (a later projection slice). Open string — never switched on.</summary>
    public string? Model { get; init; }

    /// <summary>Input (prompt) tokens the agent consumed, or null when unknown (a plain node/map agent, or a harness that reported none). Populated for SUPERVISOR-spawned agents off the durable ledger — no extra query.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Output (completion) tokens the agent produced, or null when unknown. See <see cref="InputTokens"/>.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>The agent's run DURATION in milliseconds — final (<c>CompletedAt − StartedAt</c>) once terminal, else live elapsed (<c>now − StartedAt</c>) computed at projection time; null when the run hasn't started yet or for a non-supervisor agent. A LIVE figure (recomputed every phase read), NOT a replay-deterministic one. Feeds the collapsed phase table's Time column. Open numeric — never switched on.</summary>
    public long? DurationMs { get; init; }

    /// <summary>How many SIDE-EFFECTING tool calls the agent made — its <c>tool_call_ledger</c> rows minus the <c>decision.request</c> HITL envelopes. NOTE the ledger records only side-effecting tools (read-only reads/greps are never ledgered), so this is "mutations attempted", not a total tool-use count. <c>0</c> is a real "made none"; null only when the agent row is absent. Feeds the collapsed phase table's Tools column.</summary>
    public int? ToolCount { get; init; }

    /// <summary>The agent's REALIZED spend in USD — <c>model price × tokens</c>, computed once server-side so every surface shows the same figure. Null when the model is unpriced (fail-open — e.g. a Codex/OpenAI model absent from the price table) or before tokens land. Open numeric — display as money, never switched on.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>How many files the agent changed — the GIT-TRUTH count off the result's <c>changedFiles</c> (not a live FileChanged-event tally, which can double-count). Null before the result lands / when the agent row is absent; <c>0</c> is a real "touched none". Feeds the terminal's files fact.</summary>
    public int? FilesChanged { get; init; }

    /// <summary>The agent's OWN changed-file paths (bounded) — the git-truth <c>changedFiles</c> list, so the terminal's Files tab lists them in EVERY surface (run detail + the session room), not only where the room injects them. Empty before the result lands / when it touched none.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The agent's own one-line RESULT takeaway (its model-authored <c>summary</c> off the folded compact) — the human "what this agent concluded", shown before any raw log. Null before the result lands / when the agent produced none.</summary>
    public string? Summary { get; init; }
}
