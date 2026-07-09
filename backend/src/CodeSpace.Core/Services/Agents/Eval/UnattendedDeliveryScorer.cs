using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// The pure scorer turning a team's runs into an <see cref="UnattendedDeliveryScorecard"/> — the measurement of the
/// path-to-intelligence north-star: "task in → merged/published artifact out with zero human touches." The
/// generic sibling of <see cref="SupervisorEvalScorecard"/> — it scores EVERY run (single-agent or
/// supervisor-orchestrated) identically because its inputs (solved / delivered / human touches / cost) are already
/// resolved off the shared PublishManifest + human-touch ledgers, not a supervisor-specific decision tape.
/// Deterministic + DB-free, so it unit-tests exhaustively.
/// </summary>
public static class UnattendedDeliveryScorer
{
    public static UnattendedDeliveryScorecard Compute(IReadOnlyList<UnattendedDeliveryRunOutcome> runs) =>
        Build(runs.Select(Score).ToList());

    /// <summary>Score ONE run — pure. The headline bit is solved AND delivered AND zero human touches.</summary>
    public static UnattendedDeliveryRunScore Score(UnattendedDeliveryRunOutcome run) => new()
    {
        WorkflowRunId = run.WorkflowRunId,
        Solved = run.Solved,
        Delivered = run.Delivered,
        HumanTouches = run.HumanTouches,
        CostUsd = run.CostUsd,
        UnattendedSolvedWithDelivery = run.Solved && run.Delivered && run.HumanTouches == 0,
    };

    /// <summary>Fold the per-run scores into the cross-run roll-up. <see cref="UnattendedDeliveryRollup.TotalCostUsd"/> sums only the priced runs; null when none were priceable (never a silent $0).</summary>
    private static UnattendedDeliveryScorecard Build(IReadOnlyList<UnattendedDeliveryRunScore> scores)
    {
        var total = scores.Count;
        var priced = scores.Where(s => s.CostUsd is not null).ToList();

        var rollup = new UnattendedDeliveryRollup
        {
            TotalRuns = total,
            SolvedRuns = scores.Count(s => s.Solved),
            DeliveredRuns = scores.Count(s => s.Delivered),
            UnattendedSolvedWithDeliveryRuns = scores.Count(s => s.UnattendedSolvedWithDelivery),
            UnattendedSolveWithDeliveryRate = total == 0 ? 0 : (double)scores.Count(s => s.UnattendedSolvedWithDelivery) / total,
            SolveRate = total == 0 ? 0 : (double)scores.Count(s => s.Solved) / total,
            DeliveryRate = total == 0 ? 0 : (double)scores.Count(s => s.Delivered) / total,
            AvgHumanTouches = total == 0 ? 0 : scores.Average(s => s.HumanTouches),
            TotalCostUsd = priced.Count == 0 ? null : priced.Sum(s => s.CostUsd!.Value),
            UnknownCostRuns = total - priced.Count,
        };

        return new UnattendedDeliveryScorecard { Rollup = rollup, Runs = scores };
    }
}
