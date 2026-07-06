using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each MODEL-critic review beat (the <c>DecisionReviewTimelineSource</c>'s synthetic events) with the
/// verdict card — approved/flagged, rationale, evidence-attached issues, and the DISCARDED DRAFT's attribution
/// (the once-anonymous "model call · N tokens" now reads as "the flagged draft") — keyed by the same deterministic
/// id the timeline map emits. <c>ReviewerRunId</c> stays null on purpose: a model critic has no run to deep-link,
/// and the card renders the "model critic — independently prompted" independence line instead.
/// </summary>
public sealed class DecisionReviewFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;

    public DecisionReviewFactsSource(ISupervisorDecisionLog decisions) { _decisions = decisions; }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var decision in tape)
        {
            var reviews = SupervisorOutcome.ReadReviews(decision.OutcomeJson);

            for (var i = 0; i < reviews.Count; i++)
            {
                var r = reviews[i];

                facts[DecisionReviewTimelineMap.EventId(decision.Id, i)] = new JournalStepFacts
                {
                    Review = new JournalReviewVerdict
                    {
                        Approved = r.Approved,
                        Rationale = r.Rationale,
                        Issues = r.Issues,
                        ReviewerRunId = null,
                        ReviewerHarness = null,
                        Scope = r.Scope,
                        DraftAttribution = r.DraftAttribution,
                    },
                };
            }
        }

        return facts;
    }
}
