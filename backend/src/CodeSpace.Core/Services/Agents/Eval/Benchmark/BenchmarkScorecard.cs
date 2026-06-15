using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>
/// Reduces a set of <see cref="BenchmarkResult"/> into the EXISTING <see cref="AgentRunScorecard"/> shape via the
/// SAME pure <see cref="EvalScorecard.Compute"/> — so a benchmark comparison renders as comparable per-MODE rows
/// (<c>bench:cli</c> / <c>bench:cli-mcp</c> / <c>bench:workflow-map</c>) on the same scorecard the operator already
/// reads, with ZERO change to that scorer or to PR-A's team-history scorecard. The scorer's own doc anticipated
/// exactly this: "a future fixed-task eval bench reduces into the very same scorecard."
///
/// <para><b>The one honest twist:</b> a benchmark row's <c>SuccessRate</c> is the SOLVE rate (the objective grade),
/// not the mere run-completion rate. We project each result with <see cref="AgentRunStatus.Succeeded"/> ONLY when
/// the grader PASSED — a run that Succeeded but failed the tests is scored as a non-success here, because the whole
/// point of the instrument is to measure solving, not finishing. The mode label is the grouping key, so the
/// per-row breakdown is per-mode; the overall rollup is the cross-mode solve rate.</para>
/// </summary>
public static class BenchmarkScorecard
{
    public static AgentRunScorecard Compute(IReadOnlyList<BenchmarkResult> results) =>
        EvalScorecard.Compute(results.Select(ToOutcome).ToList());

    /// <summary>
    /// Project a benchmark result onto the pure scorer's input: the row label is the MODE (so rows group per-mode),
    /// and the scored status is Succeeded IFF the grader passed — the solve-rate semantics. A non-passing result
    /// keeps the run's own terminal status (Failed / TimedOut / Cancelled) so the row still counts it as a scored
    /// non-success. Duration carries straight through for the latency percentiles.
    /// </summary>
    private static AgentRunOutcome ToOutcome(BenchmarkResult result) => new()
    {
        Harness = BenchmarkModeLabel.For(result.Mode),
        Status = result.Grade.Passed ? AgentRunStatus.Succeeded : NonSuccessStatus(result.RunStatus),
        DurationSeconds = result.DurationSeconds,
    };

    /// <summary>The status a NON-passing result scores as: the run's own terminal status when it's already a terminal non-success; otherwise Failed (a run that Succeeded but failed the grade is a solve-failure, scored Failed, never Succeeded).</summary>
    private static AgentRunStatus NonSuccessStatus(AgentRunStatus runStatus) =>
        runStatus is AgentRunStatus.Failed or AgentRunStatus.TimedOut or AgentRunStatus.Cancelled ? runStatus : AgentRunStatus.Failed;
}
