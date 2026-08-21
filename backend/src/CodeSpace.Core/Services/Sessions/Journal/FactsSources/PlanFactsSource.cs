using System.Globalization;
using System.Text;
using CodeSpace.Core.Services.Supervisor.Observation;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Dtos.Workflows.Supervisor;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each supervisor PLAN decision step with the subtasks the model authored — the plan itself, read off the Plan
/// decision's bounded observation leaves and keyed by the decision's timeline event id, so the walk hangs it on the SAME "planned the work" step the
/// supervisor describer produced. The plan then renders inline under its own PLAN beat — the causal spine plan → dispatch
/// → agents — instead of floating away from the decision that authored it. A re-plan is another Plan decision with its own
/// id, so its subtasks attach to that later step automatically. One request reads at most 500 latest Plan decisions and
/// never follows Older; caps or invalid/corrupt leaves become typed coverage instead of partial Plan facts.
/// </summary>
public sealed class PlanFactsSource : IJournalFactsSource
{
    private readonly ISupervisorPlanObservationPageBundle _plans;

    public PlanFactsSource(ISupervisorPlanObservationPageBundle plans)
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
            var key = SupervisorDecisionTimelineMap.EventId(decision.Metadata.DecisionId);
            if (decision.Metadata.Status is SupervisorDecisionObservationStatus.Corrupt or SupervisorDecisionObservationStatus.LegacyUnknown)
            {
                Merge(facts, key, new JournalStepFacts { ObservationCoverage = [Coverage(decision, JournalObservationCoverageSourceKinds.SupervisorPlanMetadata, JournalObservationCoverageReason.CorruptDecisionStatus, new CoverageCounts(0, 1))] });
                continue;
            }

            if (decision.SubtasksState == SupervisorPlanObservationLeafState.Exact && IsExact(decision))
            {
                var subtasks = decision.Subtasks.Select(subtask => new JournalSubtask { SubtaskId = subtask.IdPrefix, Title = subtask.TitlePrefix }).ToList();
                if (subtasks.Count > 0) Merge(facts, key, new JournalStepFacts { Plan = subtasks });
                continue;
            }

            if (decision.SubtasksState == SupervisorPlanObservationLeafState.Missing && IsEmpty(decision)) continue;

            if (decision.SubtasksState is SupervisorPlanObservationLeafState.Invalid or SupervisorPlanObservationLeafState.Truncated or SupervisorPlanObservationLeafState.Corrupt)
            {
                var honestTruncation = decision.SubtasksState == SupervisorPlanObservationLeafState.Truncated && IsTruncated(decision);
                var reason = decision.SubtasksState == SupervisorPlanObservationLeafState.Truncated && !honestTruncation
                    ? JournalObservationCoverageReason.CorruptLeaf
                    : Reason(decision.SubtasksState);
                var observed = honestTruncation ? decision.Subtasks.Count : 0;
                var omitted = honestTruncation ? decision.SubtasksOmittedCount : decision.SubtasksTotalCount;
                Merge(facts, key, new JournalStepFacts { ObservationCoverage = [Coverage(decision, JournalObservationCoverageSourceKinds.SupervisorPlanSubtasks, reason, new CoverageCounts(observed, omitted))] });
            }
            else
            {
                Merge(facts, key, new JournalStepFacts { ObservationCoverage = [Coverage(decision, JournalObservationCoverageSourceKinds.SupervisorPlanSubtasks, JournalObservationCoverageReason.CorruptLeaf, new CoverageCounts(0, decision.SubtasksTotalCount))] });
            }
        }

        if (page.HasMore && page.Items.Count > 0)
        {
            var boundary = page.Items[0];
            var key = SupervisorDecisionTimelineMap.EventId(boundary.Metadata.DecisionId);
            Merge(facts, key, new JournalStepFacts
            {
                ObservationCoverage = [Coverage(boundary, JournalObservationCoverageSourceKinds.SupervisorPlanPage,
                    JournalObservationCoverageReason.OlderItemsOmitted, new CoverageCounts(page.Items.Count, 1, LowerBound: true))],
            });
        }

        return facts;
    }

    internal static JournalObservationCoverage Coverage(SupervisorPlanObservationItem decision, string sourceKind, JournalObservationCoverageReason reason, CoverageCounts counts) => new()
    {
        SourceKind = sourceKind,
        Reason = reason,
        ObservedCount = Math.Clamp(counts.Observed, 0, SupervisorDecisionObservationPageLimits.MaximumLimit),
        OmittedCount = Math.Max(counts.Omitted, 0),
        OmittedCountIsLowerBound = counts.LowerBound,
        DecisionId = decision.Metadata.DecisionId,
        StoryOrder = decision.Metadata.StoryOrder.ToString(CultureInfo.InvariantCulture),
    };

    internal static JournalObservationCoverageReason Reason(SupervisorPlanObservationLeafState state) => state switch
    {
        SupervisorPlanObservationLeafState.Invalid => JournalObservationCoverageReason.InvalidLeaf,
        SupervisorPlanObservationLeafState.Truncated => JournalObservationCoverageReason.TruncatedLeaf,
        _ => JournalObservationCoverageReason.CorruptLeaf,
    };

    private static bool IsExact(SupervisorPlanObservationItem decision) =>
        decision.SubtasksTotalCount == decision.Subtasks.Count && decision.SubtasksOmittedCount == 0
        && decision.Subtasks.Count <= SupervisorPlanObservationLeafLimits.MaximumSubtasks
        && decision.Subtasks.All(subtask => subtask.IdTotalBytes == Encoding.UTF8.GetByteCount(subtask.IdPrefix)
            && subtask.TitleTotalBytes == Encoding.UTF8.GetByteCount(subtask.TitlePrefix));

    private static bool IsEmpty(SupervisorPlanObservationItem decision) =>
        decision.SubtasksTotalCount == 0 && decision.SubtasksOmittedCount == 0 && decision.Subtasks.Count == 0;

    private static bool IsTruncated(SupervisorPlanObservationItem decision)
    {
        if (decision.SubtasksTotalCount < decision.Subtasks.Count || decision.SubtasksOmittedCount != decision.SubtasksTotalCount - decision.Subtasks.Count
            || decision.Subtasks.Count > SupervisorPlanObservationLeafLimits.MaximumSubtasks) return false;
        var leafLengthsValid = decision.Subtasks.All(subtask => subtask.IdTotalBytes >= Encoding.UTF8.GetByteCount(subtask.IdPrefix)
            && subtask.TitleTotalBytes >= Encoding.UTF8.GetByteCount(subtask.TitlePrefix));
        var leafWasTruncated = decision.Subtasks.Any(subtask => subtask.IdTotalBytes > Encoding.UTF8.GetByteCount(subtask.IdPrefix)
            || subtask.TitleTotalBytes > Encoding.UTF8.GetByteCount(subtask.TitlePrefix));
        return leafLengthsValid && (decision.SubtasksOmittedCount > 0 || leafWasTruncated);
    }

    private static void Merge(Dictionary<string, JournalStepFacts> facts, string key, JournalStepFacts next) =>
        facts[key] = facts.TryGetValue(key, out var current) ? current.Merge(next) : next;

    internal readonly record struct CoverageCounts(int Observed, int Omitted, bool LowerBound = false);
}
