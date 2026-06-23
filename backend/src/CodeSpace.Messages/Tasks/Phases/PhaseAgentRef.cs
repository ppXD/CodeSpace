namespace CodeSpace.Messages.Tasks.Phases;

/// <summary>
/// A reference to ONE agent run a phase fanned out to — a map branch's agent, a supervisor spawn's child, or the
/// single agent of an agent.code node. The UI links/embeds the run's live timeline from <see cref="AgentRunId"/>.
/// <see cref="Status"/> is the GROUND-TRUTH <c>AgentRunStatus</c> NAME for BOTH the structural node source and the
/// supervisor source (read team-scoped from the <c>AgentRun</c> row, never the structural <c>NodeStatus</c> /
/// decider self-report) — open on the wire, so a new status never breaks the renderer. <see cref="Label"/> is an
/// optional cheap display label (the harness today; a richer per-agent goal is a later FE-PR concern, deliberately
/// not deserialized here).
/// </summary>
public sealed record PhaseAgentRef
{
    public required Guid AgentRunId { get; init; }

    /// <summary>The agent.code node id (or the supervisor node id) the run links back to. Null for a standalone ref.</summary>
    public string? NodeId { get; init; }

    /// <summary>The branch / turn iteration key this agent ran under (e.g. <c>map#0</c>, <c>sup#turn1#0</c>). Null for a non-iterated agent.</summary>
    public string? IterationKey { get; init; }

    /// <summary>The agent run's GROUND-TRUTH status as the <c>AgentRunStatus</c> enum NAME (open string — Queued/Running/Succeeded/Failed/Cancelled/TimedOut). Both sources read it team-scoped from the real AgentRun row; the node source falls back to the owning node's status name only when the agent row is absent.</summary>
    public required string Status { get; init; }

    /// <summary>An optional cheap display label (the harness kind today).</summary>
    public string? Label { get; init; }

    /// <summary>The model the agent ran on (e.g. a pinned <c>claude-*</c>), or null when unpinned/unknown. Populated for SUPERVISOR-spawned agents (off the folded <c>agentResults</c> compact); null for a plain node/map agent (a later projection slice). Open string — never switched on.</summary>
    public string? Model { get; init; }

    /// <summary>Input (prompt) tokens the agent consumed, or null when unknown (a plain node/map agent, or a harness that reported none). Populated for SUPERVISOR-spawned agents off the durable ledger — no extra query.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Output (completion) tokens the agent produced, or null when unknown. See <see cref="InputTokens"/>.</summary>
    public int? OutputTokens { get; init; }
}
