namespace CodeSpace.Messages.Agents;

/// <summary>
/// The COMPACT, decider-visible result of ONE agent a supervisor spawned (SOTA #2). Folded into a terminal
/// spawn/retry decision's recorded outcome at rehydrate so the supervisor decider can PERCEIVE what each of its
/// agents produced — the prerequisite for adaptively retrying a failed subtask instead of merging blindly. A
/// pure data noun (Rule 18.1): a projection of the agent's terminal <c>AgentRunResult</c> (the normalized harness
/// output) MINUS the unbounded fields — no patch, no transcript — so it stays token-cheap in the decider prompt
/// and is a pure function of immutable post-terminal state (no artifact-store resolve, replay-deterministic).
///
/// <para>Built by <c>SupervisorOutcome.ProjectCompact</c>, the single shared projector the rehydrate fold AND
/// the <c>merge</c> executor both consume, so the decider's view and the merge's view can never drift on which
/// fields an agent exposes.</para>
/// </summary>
public sealed record SupervisorAgentResult
{
    /// <summary>The spawned agent run's id (the join key back to the durable AgentRun row + the spawn outcome's agentRunIds).</summary>
    public required Guid AgentRunId { get; init; }

    /// <summary>The agent run's terminal ROW status name (e.g. "Succeeded" / "Failed" / "Cancelled" / "TimedOut") — authoritative, taken from the AgentRun row, so it is present even when the run never wrote a result (a cancelled/abandoned agent).</summary>
    public required string Status { get; init; }

    /// <summary>The agent's final summary message (null when it produced none).</summary>
    public string? Summary { get; init; }

    /// <summary>The failure detail when the agent failed — taken from the result's error, else the ROW error (a cancelled/abandoned agent sets the row error with no result). Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>The git ground-truth repo-relative paths the agent changed (never the diff body). Defaults to empty and NEVER serializes null, so a consumer can always treat it as an array.</summary>
    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    /// <summary>The branch the agent's sandbox pushed (the PR-open handoff), null when it pushed none.</summary>
    public string? ProducedBranch { get; init; }
}
