using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Tags each supervisor DECISION step with its ROUND — the decision's 1-based turn in the run's decision loop, read off
/// the ledger's <c>FenceEpoch</c> (the recorded turn number) and keyed by the decision's timeline event id
/// (<see cref="SupervisorDecisionTimelineMap.EventId"/>). So the journal reads "round 1 · planned", "round 2 · asked
/// you", … and a terminal "budget exhausted" is a plain consequence of the round count the reader can SEE — instead of a
/// bare "stopped" with no sense of how far the run got. A no-op round (an empty spawn that dispatched nothing) still
/// carries its round number, so a wasted round reads as exactly that ("round 3 · spawned no agents"). Only supervisor
/// decisions have a round; a plain agent / tool / lifecycle step contributes nothing.
/// </summary>
public sealed class SupervisorRoundFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;

    public SupervisorRoundFactsSource(ISupervisorDecisionLog decisions)
    {
        _decisions = decisions;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var decision in tape)
            facts[SupervisorDecisionTimelineMap.EventId(decision)] = new JournalStepFacts { Round = (int)decision.FenceEpoch + 1 };

        return facts;
    }
}
