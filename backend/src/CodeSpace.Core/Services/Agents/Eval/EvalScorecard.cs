using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// The pure scorer turning a set of <see cref="AgentRunOutcome"/> into an <see cref="AgentRunScorecard"/> — the
/// measurement spine for "is the agent actually working" (success rate + latency), the thing that turns "SOTA"
/// from an assertion into a number. Deterministic + DB-free, so it unit-tests exhaustively and a future fixed-task
/// eval bench (CLI vs CLI+MCP vs native loop) reduces into the very same scorecard.
///
/// Only TERMINAL runs are scored; in-flight (Queued/Running) runs are excluded from the totals. Latency
/// percentiles use nearest-rank over the runs that have a duration.
/// </summary>
public static class EvalScorecard
{
    /// <summary>The synthetic harness label for the cross-harness rollup row.</summary>
    public const string OverallLabel = "(all)";

    public static AgentRunScorecard Compute(IReadOnlyList<AgentRunOutcome> outcomes)
    {
        // The canonical terminal set (AgentRunStateMachine) — NOT a hand-rolled copy, so a new terminal (NeedsReview)
        // is counted in the denominator the moment it exists rather than silently dropped like an in-flight run.
        var terminal = outcomes.Where(o => AgentRunStateMachine.IsTerminal(o.Status)).ToList();

        var byHarness = terminal
            .GroupBy(o => o.Harness, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Score(g.Key, g.ToList()))
            .ToList();

        return new AgentRunScorecard { Harnesses = byHarness, Overall = Score(OverallLabel, terminal) };
    }

    private static HarnessScore Score(string harness, IReadOnlyList<AgentRunOutcome> runs)
    {
        var total = runs.Count;
        var succeeded = runs.Count(r => r.Status == AgentRunStatus.Succeeded);

        var durations = runs
            .Where(r => r.DurationSeconds is >= 0)
            .Select(r => r.DurationSeconds!.Value)
            .OrderBy(d => d)
            .ToList();

        return new HarnessScore
        {
            Harness = harness,
            Total = total,
            Succeeded = succeeded,
            SuccessRate = total == 0 ? 0 : (double)succeeded / total,
            P50DurationSeconds = Percentile(durations, 50),
            P95DurationSeconds = Percentile(durations, 95),
        };
    }

    /// <summary>Nearest-rank percentile over a pre-sorted ascending list; null when empty. Deterministic. Internal so the sibling per-agent scorer (<see cref="AgentStatsScorer"/>) reuses the SAME percentile definition — no drift between the two scorecards.</summary>
    internal static double? Percentile(IReadOnlyList<double> sorted, int p)
    {
        if (sorted.Count == 0) return null;

        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
