using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Dispatches a merged-timeline event to the FIRST <see cref="IJournalStepDescriber"/> that claims it, else the
/// mandatory <see cref="IJournalFallbackDescriber"/> — so every event becomes a step and NONE is ever silently dropped
/// (the genericity guarantee). Autofac injects EVERY registered describer into the <c>IEnumerable</c>, so a new describer
/// is purely a dropped impl — the registry never names a concrete one (Rule 7, mirrors <c>RunTimelineProjector</c>).
/// The fallback is a SEPARATE dependency (its own interface), so it can never accidentally sit in the specific list nor
/// be shadowed by it. Pure + singleton — the walk calls it per event.
/// </summary>
public sealed class JournalStepDescriberRegistry : IJournalStepDescriberRegistry, ISingletonDependency
{
    private readonly IReadOnlyList<IJournalStepDescriber> _describers;
    private readonly IJournalFallbackDescriber _fallback;

    public JournalStepDescriberRegistry(IEnumerable<IJournalStepDescriber> describers, IJournalFallbackDescriber fallback)
    {
        _describers = describers.ToList();
        _fallback = fallback;
    }

    public JournalStep Describe(RunTimelineEvent e)
    {
        foreach (var describer in _describers)
            if (describer.CanDescribe(e))
                return describer.Describe(e);

        return _fallback.Describe(e);
    }
}
