using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each REVIEW beat (the synthetic <c>review.verdict</c> event, one per landed reviewer verdict) with the
/// PARSED verdict — approved/flagged, the rationale, the evidence-attached issues, and the reviewer run to deep-link —
/// keyed by the SAME deterministic id the timeline source emits (<see cref="ReviewVerdictTimelineMap.EventId"/>), so
/// the walk hangs it on the exact beat regardless of which harness the reviewer ran on. A reviewer run still in
/// flight / off-contract contributes nothing.
/// </summary>
public sealed class ReviewVerdictFactsSource : IJournalFactsSource
{
    private readonly ReviewerVerdictReader _verdicts;

    public ReviewVerdictFactsSource(ReviewerVerdictReader verdicts) { _verdicts = verdicts; }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _verdicts.ReadForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => ReviewVerdictTimelineMap.EventId(r.Verdict.ReviewerRunId!.Value), r => new JournalStepFacts { Review = r.Verdict });
    }
}
