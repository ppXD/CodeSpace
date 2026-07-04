using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each supervisor PLAN decision step with the subtasks the model authored — the plan itself, read off the Plan
/// decision's PAYLOAD (<see cref="SupervisorOutcome.ReadPlanSubtasks"/>) and keyed by the decision's timeline event id
/// (<see cref="SupervisorDecisionTimelineMap.EventId"/>), so the walk hangs it on the SAME "planned the work" step the
/// supervisor describer produced. The plan then renders inline under its own PLAN beat — the causal spine plan → dispatch
/// → agents — instead of floating away from the decision that authored it. A re-plan is another Plan decision with its own
/// id, so its subtasks attach to that later step automatically. Only Plan decisions contribute; a run with no plan adds nothing.
/// </summary>
public sealed class PlanFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;

    public PlanFactsSource(ISupervisorDecisionLog decisions)
    {
        _decisions = decisions;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var decision in tape.Where(d => d.DecisionKind == SupervisorDecisionKinds.Plan))
        {
            var subtasks = SupervisorOutcome.ReadPlanSubtasks(decision.PayloadJson)
                .Select(s => new JournalSubtask { SubtaskId = s.Id, Title = s.Title })
                .ToList();

            if (subtasks.Count > 0)
                facts[SupervisorDecisionTimelineMap.EventId(decision)] = new JournalStepFacts { Plan = subtasks };
        }

        return facts;
    }
}
