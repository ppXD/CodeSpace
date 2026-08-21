using System.Text;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Supervisor.Observation;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each supervisor PLAN beat with the model call that AUTHORED it (model · tokens · cost) — the same "via
/// &lt;model&gt;" attribution a flow.map planner beat shows, so a reader sees HOW the plan was authored right on the beat.
/// Reads the bounded authoring-usage leaf and keys it by the decision's timeline event id. A pre-capture run (Missing
/// usage) contributes nothing; invalid/capped/corrupt leaves contribute typed coverage and never a prefix ModelCall.
/// <see cref="PlanFactsSource"/> emits their shared page-level Older omission once. Cost rides the SHARED pricing (fail-open).
/// </summary>
public sealed class SupervisorPlanModelCallFactsSource : IJournalFactsSource
{
    private readonly ISupervisorPlanObservationPageBundle _plans;

    public SupervisorPlanModelCallFactsSource(ISupervisorPlanObservationPageBundle plans)
    {
        _plans = plans;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var page = await _plans.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);
        var facts = new Dictionary<string, JournalStepFacts>();
        if (page is null) return facts;

        foreach (var decision in page.Items)
        {
            if (decision.Metadata.Status is SupervisorDecisionObservationStatus.Corrupt or SupervisorDecisionObservationStatus.LegacyUnknown) continue;
            var key = SupervisorDecisionTimelineMap.EventId(decision.Metadata.DecisionId);

            if (decision.ModelUsageState == SupervisorPlanObservationLeafState.Exact && IsExact(decision.ModelUsage))
            {
                facts[key] = new JournalStepFacts { ModelCall = ToModelCall(decision.ModelUsage) };
                continue;
            }

            if (decision.ModelUsageState == SupervisorPlanObservationLeafState.Missing && decision.ModelUsage is null) continue;

            if (decision.ModelUsageState is SupervisorPlanObservationLeafState.Invalid or SupervisorPlanObservationLeafState.Truncated or SupervisorPlanObservationLeafState.Corrupt
                || decision.ModelUsageState is SupervisorPlanObservationLeafState.Exact or SupervisorPlanObservationLeafState.Missing)
            {
                var honestTruncation = decision.ModelUsageState == SupervisorPlanObservationLeafState.Truncated && IsTruncated(decision.ModelUsage);
                var reason = decision.ModelUsageState is SupervisorPlanObservationLeafState.Exact or SupervisorPlanObservationLeafState.Missing
                    || decision.ModelUsageState == SupervisorPlanObservationLeafState.Truncated && !honestTruncation
                    ? JournalObservationCoverageReason.CorruptLeaf
                    : PlanFactsSource.Reason(decision.ModelUsageState);
                facts[key] = new JournalStepFacts
                {
                    ObservationCoverage = [PlanFactsSource.Coverage(decision, JournalObservationCoverageSourceKinds.SupervisorPlanModelUsage,
                        reason, new PlanFactsSource.CoverageCounts(honestTruncation ? 1 : 0, 0))],
                };
            }
        }

        return facts;
    }

    /// <summary>Project the folded authoring usage to the shared <see cref="JournalModelCall"/> — the SAME row a model-call fold shows, minus a latency (the authoring span isn't captured on the decision). Cost via the shared pricing, fail-open null on an unpriced model.</summary>
    private static JournalModelCall ToModelCall(SupervisorPlanModelUsageObservationLeaf usage)
    {
        var tokens = usage.InputTokens is null && usage.OutputTokens is null ? (int?)null : (usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0);

        return new JournalModelCall
        {
            Purpose = "supervisor.plan",
            Model = usage.ModelPrefix,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            Tokens = tokens,
            LatencyMs = null,
            CostUsd = tokens is null ? null : AgentCostPricing.CostUsd(usage.ModelPrefix, usage.InputTokens ?? 0, usage.OutputTokens ?? 0),
            Status = "completed",
        };
    }

    private static bool IsExact(SupervisorPlanModelUsageObservationLeaf? usage) => usage is not null
        && !string.IsNullOrWhiteSpace(usage.ModelPrefix)
        && usage.ModelTotalBytes == Encoding.UTF8.GetByteCount(usage.ModelPrefix);

    private static bool IsTruncated(SupervisorPlanModelUsageObservationLeaf? usage) => usage is not null
        && !string.IsNullOrWhiteSpace(usage.ModelPrefix)
        && usage.ModelTotalBytes > Encoding.UTF8.GetByteCount(usage.ModelPrefix);
}
