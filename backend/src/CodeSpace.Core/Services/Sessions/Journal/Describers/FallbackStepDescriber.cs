using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>
/// The MANDATORY fallback — renders ANY event no specific describer claimed (a future timeline source, a new kind) as a
/// plain generic <c>event</c> step, carrying the event's own title + tone so it still reads. This is the genericity
/// guarantee made concrete: dropping an event from the journal would be an INVISIBLE data loss, so there is no drop
/// path — an unclaimed event degrades to a legible step, never to nothing. A single impl of the distinct fallback
/// interface (so it is never in the specific-describer list).
/// </summary>
public sealed class FallbackStepDescriber : IJournalFallbackDescriber, ISingletonDependency
{
    public JournalStep Describe(RunTimelineEvent e) => JournalSteps.From(e, JournalStepKinds.Event);
}
