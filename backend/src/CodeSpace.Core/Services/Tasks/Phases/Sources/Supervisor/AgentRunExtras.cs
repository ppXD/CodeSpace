namespace CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;

/// <summary>
/// The LIVE per-agent rollup the supervisor source's DB-read half computes for one spawned agent — the figures that
/// are NOT folded into the durable ledger and so must come from the real <c>AgentRun</c> row + <c>tool_call_ledger</c>:
/// the run DURATION (live elapsed for a still-running agent, hence not replay-deterministic) and the GOVERNED tool
/// count. Passed into the pure <c>SupervisorPhaseSource.ProjectDecisions</c> so the projection stays DB-free + testable.
/// </summary>
public sealed record AgentRunExtras
{
    /// <summary>Run duration in milliseconds (final once terminal, else live elapsed), or null when the run hasn't started.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Side-effecting tool calls the agent made (its <c>tool_call_ledger</c> rows minus <c>decision.request</c>; read-only tools are never ledgered); <c>0</c> means "made none".</summary>
    public int ToolCount { get; init; }
}
