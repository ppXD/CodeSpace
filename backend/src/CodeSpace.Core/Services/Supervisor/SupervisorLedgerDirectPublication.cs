using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The shared qualification for the degenerate integration shape where one accepted work unit's own branch is the
/// run's delivered head. Both the stop gate and completion-stage trace use this exact predicate so a stop they release
/// cannot later park for allegedly missing integration.
/// </summary>
public static class SupervisorLedgerDirectPublication
{
    public static bool Qualifies(IReadOnlyList<SupervisorPriorDecision> decisions, IReadOnlySet<Guid> publishedAgentRunIds)
    {
        var frontier = SupervisorOutcome.FindUnpublishedFrontier(decisions);

        if (frontier is null || frontier.DecisionKind == SupervisorDecisionKinds.Resolve) return false;

        if (decisions.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && d.Sequence > frontier.Sequence && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is not null))
            return false;

        var acceptedWork = SupervisorOutcome.ReadAgentResults(frontier.OutcomeJson)
            .Where(r => SupervisorOutcome.ResultShowsWork(r) && !SupervisorOutcome.IsWithheldFromHead(r))
            .ToList();

        return acceptedWork.Count == 1 && publishedAgentRunIds.Contains(acceptedWork[0].AgentRunId);
    }

    internal static IReadOnlySet<Guid> FoldPublishedAgentRunIds(IReadOnlyList<PublishManifest> manifests) =>
        manifests
            .Where(m => m.Kind == PublishManifestKind.Agent && m.AgentRunId is not null)
            .GroupBy(m => m.AgentRunId!.Value)
            .Where(g => g.All(m => m.PublishStateValue == PublishState.Pushed || m.PullRequestNumber is not null))
            .Select(g => g.Key)
            .ToHashSet();
}
