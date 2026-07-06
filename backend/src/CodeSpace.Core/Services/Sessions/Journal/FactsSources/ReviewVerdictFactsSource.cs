using CodeSpace.Core.Services.Tasks.Timeline.Sources;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each REVIEW beat (a reviewer run's final-summary step, classified by <c>AgentEventStepDescriber</c>) with
/// the PARSED verdict — approved/flagged, the rationale, the evidence-attached issues, and the reviewer run to
/// deep-link — keyed by the verdict event's own timeline id, so the walk hangs it on the exact beat. The describer owns
/// the classification and the human title; THIS source owns the verdict truth (read off the reviewer's durable result,
/// never re-derived from the clamped event text, which may truncate the <c>VERDICT: {json}</c> line). A reviewer run
/// still in flight / off-contract contributes nothing — the bare review beat stands.
/// </summary>
public sealed class ReviewVerdictFactsSource : IJournalFactsSource
{
    private readonly ReviewerVerdictReader _verdicts;

    public ReviewVerdictFactsSource(ReviewerVerdictReader verdicts) { _verdicts = verdicts; }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _verdicts.ReadForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        return rows
            .Where(r => r.FinalSummaryEventId != Guid.Empty)
            .ToDictionary(r => AgentEventTimelineMap.EventId(r.FinalSummaryEventId), r => new JournalStepFacts { Review = r.Verdict });
    }
}
