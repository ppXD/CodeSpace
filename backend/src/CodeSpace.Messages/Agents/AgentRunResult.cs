using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The normalized OUTPUT contract of an agent run — what the harness produces at the end, regardless
/// of which CLI ran. Stable + versioned so a consumer (the agent.code node, audit, the UI) reads one
/// shape for every harness. B0.3 persists this alongside the run; the diff/test artifacts it points
/// at are stored via the observability/artifact layer.
/// </summary>
public sealed record AgentRunResult
{
    public required AgentRunStatus Status { get; init; }

    /// <summary>Short machine-ish reason for the terminal state (e.g. "completed", "non-zero-exit", "timed-out", "cancelled").</summary>
    public required string ExitReason { get; init; }

    /// <summary>The agent's final summary of what it did (its last assistant/summary message).</summary>
    public string? Summary { get; init; }

    /// <summary>Repo-relative paths the agent changed.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Branch the sandbox pushed, when the run produced one (the output handoff for opening a PR).</summary>
    public string? ProducedBranch { get; init; }

    public AgentTokenUsage? TokenUsage { get; init; }

    /// <summary>Failure detail when <see cref="Status"/> is <see cref="AgentRunStatus.Failed"/>.</summary>
    public string? Error { get; init; }
}

public sealed record AgentTokenUsage
{
    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }
}
