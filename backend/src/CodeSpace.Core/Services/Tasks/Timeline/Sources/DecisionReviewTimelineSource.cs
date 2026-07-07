using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The MODEL-critic review timeline source — one synthetic event per model-critic verdict folded onto a decision's
/// outcome (<see cref="SupervisorOutcome.ReadReviews"/>): the adversarial middle the tape now carries (a draft
/// flagged → revised → re-reviewed). Shares the review-verdict PROVENANCE key with the reviewer-run source, so the
/// journal renders every verdict — model or real agent — as the SAME review beat; the events carry the decision's
/// own timestamp and sort BEFORE it (the source-key tie-break), so the story reads verdict → surviving decision.
/// Contributes nothing for a run with no folded reviews (every pre-chain run, byte-identical). READ-ONLY.
/// </summary>
public sealed class DecisionReviewTimelineSource : IRunTimelineSource, IScopedDependency
{
    private readonly ISupervisorDecisionLog _decisions;

    public DecisionReviewTimelineSource(ISupervisorDecisionLog decisions) { _decisions = decisions; }

    public string SourceKey => ReviewVerdictTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        var events = new List<RunTimelineEvent>();

        foreach (var decision in tape)
        {
            var reviews = SupervisorOutcome.ReadReviews(decision.OutcomeJson);

            for (var i = 0; i < reviews.Count; i++)
                if (!reviews[i].ViaAgent)   // an agent verdict's reviewer run is already its own beat — never beat it twice
                    events.Add(DecisionReviewTimelineMap.ToEvent(decision, i, reviews[i]));
        }

        return events;
    }
}

/// <summary>Pure mapping from ONE folded model-critic verdict to its synthetic timeline event — the id contract is shared with <c>DecisionReviewFactsSource</c> so the parsed verdict lands on the exact beat.</summary>
public static class DecisionReviewTimelineMap
{
    /// <summary>The deterministic event id for the i-th verdict on a decision — pinned in one place so the facts source can't drift.</summary>
    public static string EventId(Guid decisionId, int index) => $"review-d{decisionId:N}-{index}";

    public static RunTimelineEvent ToEvent(SupervisorDecisionRecord decision, int index, SupervisorDecisionReview review) => new()
    {
        Id = EventId(decision.Id, index),
        Kind = ReviewVerdictTimelineMap.VerdictKind,
        Title = TitleFor(review, index),
        Summary = review.Rationale,
        Severity = review.Approved ? TimelineSeverity.Success : TimelineSeverity.Warning,
        Level = TimelineLevel.Milestone,
        OccurredAt = decision.CreatedDate,   // same instant as the surviving decision — the review-verdict source key sorts it BEFORE the decision beat
        Order = decision.Sequence,
        SourceKey = ReviewVerdictTimelineMap.Key,
    };

    /// <summary>The verdict beat's headline — WHO (the model critic) + outcome + WHAT: the draft when one was discarded, the REVISED decision on a later rung (so the Gate ladder reads draft → revised), else the decision itself.</summary>
    internal static string TitleFor(SupervisorDecisionReview review, int index = 0)
    {
        var subject = review.Scope == "plan" ? "plan" : "decision";

        if (review.Approved) return $"Model critic approved the {(index > 0 ? "revised " : "")}{subject}";

        var what = review.DraftAttribution is not null ? $"the {subject} draft" : index > 0 ? $"the revised {subject}" : $"the {subject}";

        return $"Model critic flagged {what}{(review.Issues.Count > 0 ? $" — {review.Issues.Count} issue{(review.Issues.Count == 1 ? "" : "s")}" : "")}";
    }
}
