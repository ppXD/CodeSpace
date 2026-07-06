using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The REVIEW-VERDICT timeline source — one SYNTHETIC event per landed reviewer verdict, read off the reviewer run's
/// durable RESULT via the shared <see cref="ReviewerVerdictReader"/>. HARNESS-INDEPENDENT by design: the adversarial
/// exchange's verdict beat must not depend on which harness the reviewer ran on (codex-cli emits no final-summary
/// event — on the event log alone a real run's verdict was invisible). The event id is deterministic
/// (<see cref="ReviewVerdictTimelineMap.EventId"/>) so the journal facts source keys the parsed verdict onto the SAME
/// step. Contributes nothing for a run with no landed reviewer verdicts. READ-ONLY.
/// </summary>
public sealed class ReviewVerdictTimelineSource : IRunTimelineSource, IScopedDependency
{
    private readonly ReviewerVerdictReader _verdicts;

    public ReviewVerdictTimelineSource(ReviewerVerdictReader verdicts) { _verdicts = verdicts; }

    public string SourceKey => ReviewVerdictTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var rows = await _verdicts.ReadForRunAsync(context.RunId, context.TeamId, cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return Array.Empty<RunTimelineEvent>();

        return rows.Select(ReviewVerdictTimelineMap.ToEvent).ToList();
    }
}

/// <summary>Pure mapping from ONE landed reviewer verdict to its synthetic timeline event — extracted so the id contract + the copy are unit-testable without a database.</summary>
public static class ReviewVerdictTimelineMap
{
    /// <summary>The review-verdict source's provenance key — stamped on every event this mapper emits; the journal describer classifies by it.</summary>
    public const string Key = "review-verdict";

    /// <summary>The verdict event's kind string (open vocabulary, never switched on downstream).</summary>
    public const string VerdictKind = "review.verdict";

    /// <summary>The deterministic event id for one reviewer run's verdict — the SAME id the journal facts source keys the parsed verdict by, pinned in one place so the two can't drift.</summary>
    public static string EventId(Guid reviewerRunId) => $"review-{reviewerRunId:N}";

    public static RunTimelineEvent ToEvent(ReviewerVerdictRow row) => new()
    {
        Id = EventId(row.Verdict.ReviewerRunId!.Value),
        Kind = VerdictKind,
        Title = TitleFor(row.Verdict),
        Summary = row.Verdict.Rationale,
        Severity = row.Verdict.Approved ? TimelineSeverity.Success : TimelineSeverity.Warning,
        Level = TimelineLevel.Milestone,
        OccurredAt = row.CompletedAt,
        NodeId = row.NodeId,
        AgentRunId = row.Verdict.ReviewerRunId.ToString(),
        IterationKey = row.IterationKey,
        SourceKey = Key,
    };

    /// <summary>The verdict beat's headline — scope + outcome, never the raw contract line.</summary>
    private static string TitleFor(JournalReviewVerdict verdict)
    {
        var subject = verdict.Scope == JournalReviewVerdict.PlanScope ? "the plan" : "the produced work";

        return verdict.Approved
            ? $"Independent reviewer approved {subject}"
            : $"Independent reviewer flagged {subject}{(verdict.Issues.Count > 0 ? $" — {verdict.Issues.Count} issue{(verdict.Issues.Count == 1 ? "" : "s")}" : "")}";
    }
}
