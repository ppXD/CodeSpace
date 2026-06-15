namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The agent EXECUTION MODE a benchmark task is run through — the axis the instrument exists to compare.
/// "Mode X beats mode Y" becomes a number when the SAME task + the SAME grading oracle is run through each
/// mode and the pass-rates line up side by side on the scorecard. The modes are deliberately the three the
/// platform already ships an execution path for (Rule 3 — measure what exists, don't invent a fourth lane):
///
/// <list type="bullet">
/// <item><b>HarnessCli</b> — the bare harness CLI (codex / claude) in its sandbox, no tool fabric. The baseline.</item>
/// <item><b>HarnessCliWithMcp</b> — the same CLI WITH the run-scoped in-process MCP endpoint open
///   (<c>CODESPACE_AGENT_MCP_ENDPOINT_ENABLED</c>), so "does the tool fabric move the number" is measurable.</item>
/// <item><b>WorkflowMap</b> — the planner→<c>flow.map</c>→synthesizer composed flow (the headline path), where a
///   plan fans the task out across parallel agent branches. The PLAN-QUALITY axis (did the generated plan run
///   clean to a composed result with zero human edits) is captured for this mode for PR-D's gate.</item>
/// </list>
/// </summary>
public enum BenchmarkMode
{
    /// <summary>The bare harness CLI in its sandbox — no tool fabric. The comparison baseline.</summary>
    HarnessCli,

    /// <summary>The harness CLI with the run-scoped MCP tool-fabric endpoint open — isolates "does MCP help".</summary>
    HarnessCliWithMcp,

    /// <summary>The planner→flow.map→synthesizer composed flow — isolates "does planning + fan-out help" (+ the plan-quality axis).</summary>
    WorkflowMap,
}
