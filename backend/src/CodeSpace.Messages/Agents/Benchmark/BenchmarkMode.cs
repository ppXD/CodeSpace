namespace CodeSpace.Messages.Agents.Benchmark;

/// <summary>
/// The agent EXECUTION MODE a benchmark task is run through — the axis the instrument exists to compare.
/// "Mode X beats mode Y" becomes a number when the SAME task + the SAME grading oracle is run through each
/// mode and the pass-rates line up side by side on the scorecard. The modes are deliberately ones the platform
/// already ships an execution path for (Rule 3 — measure what exists, don't invent a new lane):
///
/// <list type="bullet">
/// <item><b>HarnessCli</b> — the bare harness CLI (codex / claude) in its sandbox, no tool fabric. The baseline.</item>
/// <item><b>HarnessCliWithMcp</b> — the same CLI WITH the run-scoped in-process MCP endpoint open (the per-run
///   <c>AgentTask.EnableMcpEndpoint</c> opt-in the benchmark runner sets), so "does the tool fabric move the number"
///   is measurable: the two CLI modes run the SAME task in the SAME process and differ observably on whether the
///   fabric is reachable, not merely on the scorecard label.</item>
/// <item><b>WorkflowMap</b> — RESERVED, NOT YET WIRED: the planner→<c>flow.map</c>→synthesizer composed flow (the
///   headline path) where a plan fans the task out across parallel agent branches. It runs through the workflow
///   ENGINE, not a single agent run, so the single-run <c>BenchmarkRunner</c> does not drive it and the seed corpus
///   does not list it (<c>SeedBenchmarkCorpus.DefaultModes</c>); it lands with the workflow-driving harness. The
///   PLAN-QUALITY axis (<c>BenchmarkResult.PlanRanCleanWithNoHumanEdits</c>) is defined now for that future mode.</item>
/// </list>
/// </summary>
public enum BenchmarkMode
{
    /// <summary>The bare harness CLI in its sandbox — no tool fabric. The comparison baseline.</summary>
    HarnessCli,

    /// <summary>The harness CLI with the run-scoped MCP tool-fabric endpoint open — isolates "does MCP help".</summary>
    HarnessCliWithMcp,

    /// <summary>RESERVED, not yet wired: the planner→flow.map→synthesizer composed flow (workflow-engine driven, not a single agent run). Requesting it from the single-run runner throws; the seed corpus omits it until the workflow-driving harness lands.</summary>
    WorkflowMap,
}
