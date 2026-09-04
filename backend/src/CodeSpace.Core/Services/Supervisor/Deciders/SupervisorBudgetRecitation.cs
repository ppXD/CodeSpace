using System.Text;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// P3.5 — the BUDGET recitation block, the cost-cap sibling of <see cref="SupervisorRecitation"/>'s plan block:
/// a compact restatement of realized spend vs. the run's <c>MaxCostUsd</c> cap, injected at the SAME prompt tail so
/// the model can see its own remaining budget and self-moderate (stop spawning expensive agents, wrap up cheaply)
/// BEFORE the server ever has to force-stop it. An UNCAPPED run renders the cap-less spend block instead (D6) —
/// the figure is what the brain moderates against, and only the cap LINE needs a cap. Null when the run is both
/// uncapped and has spent nothing, mirroring <see cref="SupervisorRecitation"/>'s own null-when-inapplicable contract.
///
/// <para>The SAME text this class renders also rides as the cost-cap stop decision's <c>detail</c> field (see
/// <see cref="SupervisorTurnService"/>'s force-stop site) — one renderer, so the model's own recitation and the
/// operator-facing stop reason can never disagree about the numbers.</para>
/// </summary>
public static class SupervisorBudgetRecitation
{
    /// <summary>The block's pinned header — a stable prompt landmark (tests + the model key on it), mirroring <see cref="SupervisorRecitation.Header"/>.</summary>
    public const string Header = "BUDGET (recite before deciding — spend above the cap force-stops the run):";

    /// <summary>The CAP-LESS sibling header (D6) — an uncapped run has no ceiling to recite, so the block states spend and claims nothing about a limit the operator never set.</summary>
    public const string UncappedHeader = "SPEND SO FAR (this run has no cost ceiling — no spend figure will force-stop it):";

    /// <summary>
    /// Render the budget block for the decider's prompt: the cap block when a cost cap is set, else — D6 — the
    /// bare spend block once anything has actually been spent. Null only for a run that is BOTH uncapped and has
    /// spent nothing (a fresh turn 1), which keeps that prompt byte-identical.
    ///
    /// <para>D6's defect: the old null-when-uncapped rule meant the COMMON case (no <c>MaxCostUsd</c>) never showed
    /// the brain a cent of its own realized spend. A cap is what the cap LINE needs; the spend figure needs only
    /// spend.</para>
    /// </summary>
    public static string? Render(decimal? maxCostUsd, decimal agentExecutionSpendUsd, decimal brainPlaneSpendUsd, IReadOnlyDictionary<string, decimal> brainPlaneSpendByKind)
    {
        if (maxCostUsd is { } cap)
        {
            var capped = new StringBuilder(Header);
            capped.AppendLine().Append(Summary(cap, agentExecutionSpendUsd, brainPlaneSpendUsd, brainPlaneSpendByKind));

            return capped.ToString();
        }

        var total = agentExecutionSpendUsd + brainPlaneSpendUsd;

        if (total <= 0) return null;

        var builder = new StringBuilder(UncappedHeader);
        builder.AppendLine().Append(WithLanes($"${total:0.00} spent so far", agentExecutionSpendUsd, brainPlaneSpendByKind));

        return builder.ToString();
    }

    /// <summary>
    /// The one-line spend summary shared by the recitation block AND the cost-cap stop decision's detail — "$X.XX
    /// spent of $Y.YY cap ($Z.ZZ remaining)" plus a per-lane breakdown (agent execution + every recorded brain-plane
    /// kind, e.g. "supervisor.decision", "critic.review", "grader.acceptance"). A lane with $0 recorded spend is
    /// omitted from the breakdown (never a noisy "$0.00" entry for a kind that never ran).
    /// </summary>
    public static string Summary(decimal maxCostUsd, decimal agentExecutionSpendUsd, decimal brainPlaneSpendUsd, IReadOnlyDictionary<string, decimal> brainPlaneSpendByKind)
    {
        var total = agentExecutionSpendUsd + brainPlaneSpendUsd;
        var remaining = maxCostUsd - total;

        var headline = remaining >= 0
            ? $"${total:0.00} spent of ${maxCostUsd:0.00} cap (${remaining:0.00} remaining)"
            : $"${total:0.00} spent of ${maxCostUsd:0.00} cap (${-remaining:0.00} OVER)";

        return WithLanes(headline, agentExecutionSpendUsd, brainPlaneSpendByKind);
    }

    /// <summary>Append the per-lane breakdown to a headline — the ONE lane renderer both the capped and the uncapped block share, so the two can never disagree about how spend is attributed. A $0 lane is omitted (never a noisy "$0.00" entry for a kind that never ran).</summary>
    private static string WithLanes(string headline, decimal agentExecutionSpendUsd, IReadOnlyDictionary<string, decimal> brainPlaneSpendByKind)
    {
        var lanes = new List<string>();

        if (agentExecutionSpendUsd > 0) lanes.Add($"agent execution ${agentExecutionSpendUsd:0.00}");

        foreach (var (kind, usd) in brainPlaneSpendByKind.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            if (usd > 0) lanes.Add($"{kind} ${usd:0.00}");

        return lanes.Count == 0 ? headline : $"{headline} — {string.Join(", ", lanes)}";
    }
}
