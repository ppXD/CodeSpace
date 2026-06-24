using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Tasks.Phases;

/// <summary>
/// The per-agent rollup the run-detail outline + terminal surface for ANY agent (not just supervisor-spawned) — read
/// team-scoped off the real <c>AgentRun</c> row + its tool-call ledger by <see cref="AgentMetricsReader"/>.
/// <see cref="Status"/> is the ground-truth <see cref="AgentRunStatus"/>; <see cref="DurationMs"/> is LIVE (recomputed
/// each read); tokens come from the completed <c>ResultJson</c> (null until done, or when the harness reported none);
/// <see cref="Model"/> from the task envelope (null when unpinned). <see cref="ToolCount"/> is a real 0+ count of the
/// agent's side-effecting tool calls. The non-status figures map 1:1 onto <c>PhaseAgentRef</c>'s metric fields.
/// </summary>
public sealed record AgentRunMetrics
{
    public required AgentRunStatus Status { get; init; }

    public long? DurationMs { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int ToolCount { get; init; }

    public string? Model { get; init; }
}
