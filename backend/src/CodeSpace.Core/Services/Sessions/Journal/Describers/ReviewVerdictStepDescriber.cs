using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>
/// Describes a synthetic review-verdict event (the <see cref="ReviewVerdictTimelineSource"/>'s one event per landed
/// reviewer verdict) as THE REVIEW BEAT — the adversarial exchange's first-class journal citizen, independent of
/// which harness the reviewer ran on. The event's own copy (scope + outcome headline, rationale detail) rides
/// through; <c>ReviewVerdictFactsSource</c> attaches the parsed verdict card by the same deterministic id.
/// </summary>
public sealed class ReviewVerdictStepDescriber : IJournalStepDescriber, ISingletonDependency
{
    public bool CanDescribe(RunTimelineEvent e) => e.SourceKey == ReviewVerdictTimelineMap.Key;

    public JournalStep Describe(RunTimelineEvent e) =>
        JournalSteps.From(e, JournalStepKinds.Review) with { Beat = true, Verb = AgentEventStepDescriber.ReviewVerb, Detail = null };
}
