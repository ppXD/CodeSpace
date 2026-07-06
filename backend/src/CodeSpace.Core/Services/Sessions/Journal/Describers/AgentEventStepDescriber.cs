using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>
/// Describes an agent's own narrative event as a journal step. A REASONING event (the agent's thinking block) becomes a
/// <c>thinking</c> step — the folded chain-of-thought beat — so it reads distinctly from the agent's <c>agent</c> narrative
/// events (a file edit, a test result, an error, its final summary). Sub-classifies on the emitted kind, mirroring how the
/// lifecycle describer splits <c>model_call</c> out of run-record events.
///
/// <para>THE ADVERSARIAL EXCHANGE reads through the same lens: an event of a REVIEWER run (its cell key carries the
/// <c>#review</c> / <c>#plan-review</c> suffix) classifies as a <c>review</c> step — its final summary is the VERDICT
/// BEAT (the raw <c>VERDICT: {json}</c> line is replaced by a human title; <c>ReviewVerdictFactsSource</c> attaches the
/// parsed verdict) — and a producer's revise-round announcement (the pinned S6 Warning) is the amber REVISE beat. So a
/// reviewed run's journal reads work → review → revise → review, as first-class beats.</para>
/// </summary>
public sealed class AgentEventStepDescriber : IJournalStepDescriber, ISingletonDependency
{
    /// <summary>The REVIEW beat's semantic verb — the frontend's purple pill.</summary>
    internal const string ReviewVerb = "review";

    /// <summary>The REVISE beat's semantic verb — the frontend's amber pill.</summary>
    internal const string ReviseVerb = "revise";

    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == AgentEventTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e)
    {
        if (IsReviewerEvent(e)) return DescribeReviewer(e);

        if (IsReviseAnnouncement(e))
            return JournalSteps.From(e, JournalStepKinds.Revise) with { Beat = true, Verb = ReviseVerb, Milestone = true };

        return JournalSteps.From(e, e.Kind == AgentEventTimelineMap.ReasoningKind ? JournalStepKinds.Thinking : JournalStepKinds.Agent);
    }

    /// <summary>
    /// A reviewer run's event — every one classifies <c>review</c> so the fold groups them as reviewer background.
    /// The FINAL SUMMARY still replaces its raw title (the <c>VERDICT: {json}</c> contract line must never leak) but
    /// is NOT the beat: THE verdict beat is the synthetic <c>review.verdict</c> event
    /// (<see cref="ReviewVerdictStepDescriber"/>), read off the durable result so it surfaces for EVERY harness —
    /// codex-cli emits no final-summary event at all, which once hid a real run's verdict entirely.
    /// </summary>
    private static JournalStep DescribeReviewer(RunTimelineEvent e)
    {
        if (e.Kind != AgentEventTimelineMap.FinalSummaryKind) return JournalSteps.From(e, JournalStepKinds.Review);

        var title = IsPlanReview(e.IterationKey)
            ? "Independent reviewer verified the plan against the repository"
            : "Independent reviewer inspected the produced work";

        return JournalSteps.From(e, JournalStepKinds.Review) with { Title = title, Detail = null, Milestone = false };
    }

    /// <summary>A reviewer run's event — its cell key carries a review suffix (<c>{producer}#review</c> for an output review; the fixed <c>#plan-review</c> for a grounded plan review).</summary>
    internal static bool IsReviewerEvent(RunTimelineEvent e) =>
        e.IterationKey is { } key && (key.EndsWith(AgentOutputReviewer.IterationKeySuffix, StringComparison.Ordinal) || IsPlanReview(key));

    private static bool IsPlanReview(string? key) => key?.EndsWith(AgentPlanReviewer.IterationKey, StringComparison.Ordinal) == true;

    /// <summary>A producer's S6 revise-round announcement — the pinned-prefix Warning the executor emits per round.</summary>
    internal static bool IsReviseAnnouncement(RunTimelineEvent e) =>
        e.Kind == AgentEventTimelineMap.WarningKind && e.Title.StartsWith(AgentRunExecutor.ReviseAnnouncementPrefix, StringComparison.Ordinal);
}
