using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// One agent a supervisor decision spawned (or a retry re-ran) — the render-ready card the journal hangs off that
/// decision step, so a "spawned 3 agents" beat SHOWS the three: what each was asked (its subtask goal), how it went
/// (status), and the ground-truth of what it did (files · tokens · tool calls · duration · cost). Backend-authored off
/// the durable agent record via the shared metrics reader — the SAME numbers the phase board and the room card read, so
/// the journal can't disagree with them. A run's re-spawn WAVES need no special construct: each wave is its own later
/// spawn step carrying its own cards, in chronological order.
/// </summary>
public sealed record JournalAgentCard
{
    /// <summary>The agent run — the frontend deep-links its terminal / transcript.</summary>
    public required Guid AgentRunId { get; init; }

    /// <summary>What the agent was asked — its subtask goal (the model's decomposition). Falls back to a neutral label when the task envelope named none.</summary>
    public required string Label { get; init; }

    /// <summary>The agent's ground-truth lifecycle status (the <c>AgentRunStatus</c>).</summary>
    public required AgentRunStatus Status { get; init; }

    /// <summary>The model the agent ran on (from its task envelope). Null when unpinned.</summary>
    public string? Model { get; init; }

    /// <summary>Wall-clock in milliseconds — live elapsed while running, final once terminal. Null before it starts.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Total tokens (input + output). Null when the agent reported no usage.</summary>
    public int? Tokens { get; init; }

    /// <summary>The agent's side-effecting tool-call count. Null when its row is unreadable (0 is a real "made none").</summary>
    public int? ToolCount { get; init; }

    /// <summary>Realized spend (model × tokens), fail-open null when the model is unpriced.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Git-truth changed-file count off the result. Null before the result lands.</summary>
    public int? FilesChanged { get; init; }

    /// <summary>The agent's changed files with their +added / −removed line counts (git ground truth; a binary file's counts are null) — the diffstat ROWS the journal shows under the card. Empty for a pre-diffstat run / before the result lands, where only <see cref="FilesChanged"/> is known.</summary>
    public IReadOnlyList<FileDiffStat> Files { get; init; } = Array.Empty<FileDiffStat>();

    /// <summary>Whether this agent CONTINUED a prior conversation (a retry re-ran it resuming the earlier session) rather than starting fresh — the "⟳ resumed" chip the card shows. False for a first-run agent.</summary>
    public bool Resumed { get; init; }
}
