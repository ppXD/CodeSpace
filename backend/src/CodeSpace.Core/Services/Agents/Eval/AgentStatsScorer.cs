using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// The pure scorer turning a team's <see cref="AgentRunStatSample"/>s into a per-agent <see cref="AgentStatsRollup"/> —
/// the per-persona analogue of <see cref="EvalScorecard"/>. Deterministic + DB-free, so it unit-tests exhaustively.
///
/// <para>Grouped by <see cref="AgentRunStatSample.AgentDefinitionId"/>. Only TERMINAL runs count toward the success
/// rate + latency (the same <see cref="AgentRunStateMachine"/> terminal set the harness scorecard uses — not a
/// hand-rolled copy). The recent-outcomes sparkline includes in-flight runs so a running agent shows a live dot the
/// rate doesn't count. Spend mirrors <c>TeamCostService</c>: summed over the cost-eligible runs, null when none was
/// priceable, with the unpriceable count carried as the honesty qualifier.</para>
/// </summary>
public static class AgentStatsScorer
{
    /// <summary>How many of a persona's most-recent runs form the outcome sparkline. Small — the roster row shows a compact trend, not the full history.</summary>
    public const int RecentOutcomeCap = 10;

    public static AgentStatsRollup Compute(IReadOnlyList<AgentRunStatSample> samples)
    {
        var agents = samples
            .GroupBy(s => s.AgentDefinitionId)
            .OrderBy(g => g.Key)
            .Select(g => ScoreAgent(g.Key, g.ToList()))
            .ToList();

        return new AgentStatsRollup { Agents = agents };
    }

    private static AgentStat ScoreAgent(Guid agentDefinitionId, IReadOnlyList<AgentRunStatSample> runs)
    {
        var terminal = runs.Where(r => AgentRunStateMachine.IsTerminal(r.Status)).ToList();

        var total = terminal.Count;
        var succeeded = terminal.Count(r => r.Status == AgentRunStatus.Succeeded);

        var durations = terminal
            .Where(r => r.DurationSeconds is >= 0)
            .Select(r => r.DurationSeconds!.Value)
            .OrderBy(d => d)
            .ToList();

        var eligible = runs.Where(r => r.CostEligible).ToList();

        var recentOutcomes = runs
            .OrderByDescending(r => r.CreatedAt)
            .Take(RecentOutcomeCap)
            .Reverse()
            .Select(r => r.Status)
            .ToList();

        return new AgentStat
        {
            AgentDefinitionId = agentDefinitionId,
            Total = total,
            Succeeded = succeeded,
            SuccessRate = total == 0 ? 0 : (double)succeeded / total,
            P50DurationSeconds = EvalScorecard.Percentile(durations, 50),
            P95DurationSeconds = EvalScorecard.Percentile(durations, 95),
            EstimatedCostUsd = SumKnown(eligible),
            UnknownCostRuns = eligible.Count(r => r.Cost is null),
            LastRunAt = runs.Max(r => r.CreatedAt),
            RecentOutcomes = recentOutcomes,
        };
    }

    /// <summary>The summed cost of the priced eligible runs, or null when NONE was priceable (all unknown) — distinct from a real $0. Mirrors <c>TeamCostService.SumKnown</c>.</summary>
    private static decimal? SumKnown(IReadOnlyList<AgentRunStatSample> eligible) =>
        eligible.Any(r => r.Cost is not null) ? eligible.Where(r => r.Cost is not null).Sum(r => r.Cost!.Value) : null;
}
