using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The recorded outcome of running ONE <see cref="BenchmarkTask"/> through ONE <see cref="BenchmarkMode"/>:
/// the agent run's terminal status + duration (the success/latency the scorecard already measures) PLUS the
/// grader's objective verdict (did the task actually get SOLVED, independent of whether the run merely
/// "Succeeded"). A run can Succeed yet fail the grade — the agent finished but didn't fix the tests — which is
/// exactly the gap a self-report scorecard misses and this oracle catches.
///
/// <para>A pure data envelope (a noun → Messages, Rule 18.1). The runner produces a list of these; the
/// benchmark scorecard reduces them per-mode. Carries the <see cref="AgentRunId"/> so a result traces back to
/// the real run + its event log.</para>
/// </summary>
public sealed record BenchmarkResult
{
    /// <summary>The task this result is for (<see cref="BenchmarkTask.Id"/>).</summary>
    public required string TaskId { get; init; }

    /// <summary>The mode the task was run through.</summary>
    public required BenchmarkMode Mode { get; init; }

    /// <summary>The agent run that executed the task (provenance — links to its event log + result). Null only if the run was never created (a setup failure).</summary>
    public Guid? AgentRunId { get; init; }

    /// <summary>Terminal status of the agent run — what the existing scorecard scores as success/failure.</summary>
    public required AgentRunStatus RunStatus { get; init; }

    /// <summary>Wall-clock seconds the run took; null when it never started/completed (excluded from latency stats, mirroring <see cref="AgentRunOutcome"/>).</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>The OBJECTIVE grade: did the oracle judge the task solved. The honest "is the agent actually good" signal, distinct from <see cref="RunStatus"/>.</summary>
    public required BenchmarkGrade Grade { get; init; }

    /// <summary>
    /// Total tokens the run BILLED (input+output, summed across every revise round the executor ran). Null when the run
    /// reported no usage (the deterministic fake CLI emits none). COST NOTE for a critic A/B: this EXCLUDES the critic's
    /// OWN review model tokens — on a standalone benchmark run (<c>workflowRunId=null</c>) the critic's usage lands
    /// nowhere, so an arm's true cost is strictly ≥ the sum of this field. Projected from the run's <c>AgentRunResult.TokenUsage</c>.
    /// </summary>
    public AgentTokenUsage? TokenUsage { get; init; }

    /// <summary>How many bounded revise rounds the executor ran inside this run (<c>AgentRunResult.ReviseRounds</c>). In a critic-on arm this is the retry a critic flag bought; 0 in a critic-off arm. The retry-share disclosure that keeps an A/B honest — a solve-rate lift riding on extra attempts is visible here, not hidden.</summary>
    public int ReviseRounds { get; init; }

    /// <summary>The run's terminal <c>AgentRunResult.ExitReason</c>. Scopes an intervention proxy to the CRITIC-flag path (<c>"output-flagged"</c>) so it is never conflated with the arm-symmetric <c>"stalled"</c> harness-noise path. Null when the run recorded no result (a setup/infra failure before completion).</summary>
    public string? ExitReason { get; init; }

    /// <summary>
    /// Whether the run-scoped MCP tool-fabric endpoint was actually OPENED for this run — the resolved state of the
    /// executor's per-run gate, recorded by the runner from the SAME gate the executor used so a row can never be
    /// mislabeled. This is the load-bearing distinction between <see cref="BenchmarkMode.HarnessCli"/> (false) and
    /// <see cref="BenchmarkMode.HarnessCliWithMcp"/> (true): the two modes run the SAME task in the SAME process and
    /// differ observably here, not merely in the scorecard label, so "does the tool fabric move the number" is a real
    /// side-by-side comparison rather than two identically-executed rows.
    /// </summary>
    public required bool McpEndpointEnabled { get; init; }

    /// <summary>
    /// PLAN-QUALITY hook (defined now for PR-D's plan-review gate; only meaningful for <see cref="BenchmarkMode.WorkflowMap"/>):
    /// did the generated plan run all the way to a successful composed result with ZERO human edits. Null for the
    /// non-planning modes (no plan to assess) and until PR-D wires the no-human-edits signal — defining the field now
    /// keeps it a non-breaking add later rather than a result-shape change. <c>true</c> = clean autonomous plan;
    /// <c>false</c> = the plan needed an edit / didn't compose; <c>null</c> = not applicable / not yet measured.
    /// </summary>
    public bool? PlanRanCleanWithNoHumanEdits { get; init; }
}
