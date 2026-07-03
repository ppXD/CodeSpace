using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Default <see cref="IJournalWalk"/> — a thin composition (Rule 16): project the merged timeline, describe each event
/// through the registry, stamp each step's stable cursor. All the ORDERING lives upstream in the timeline projector (one
/// merge discipline, so the journal and the Activity tab can't disagree on order); all the CLASSIFICATION lives in the
/// pure describers. This service only threads them + stamps the opaque <see cref="JournalStep.Cursor"/> from each event's
/// own sort key (via <see cref="JournalCursor"/>) — nothing else, so a new source / describer changes the journal with
/// ZERO edit here.
/// </summary>
public sealed class JournalWalk : IJournalWalk, IScopedDependency
{
    private readonly IRunTimelineProjector _timeline;
    private readonly IJournalStepDescriberRegistry _registry;

    public JournalWalk(IRunTimelineProjector timeline, IJournalStepDescriberRegistry registry)
    {
        _timeline = timeline;
        _registry = registry;
    }

    public async Task<IReadOnlyList<JournalStep>?> WalkAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var events = await _timeline.ProjectAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (events is null) return null;   // a foreign / absent run — conflate to null exactly like the timeline projector

        // Stamp each step's cursor from its EVENT's sort key (not its walk position), so a step's cursor is stable across
        // re-walks — an earlier event backfilling mid-timeline never renumbers the steps around it. Order is the
        // projector's (the events arrive merged); the walk adds no sort of its own.
        return events.Select(e => _registry.Describe(e) with { Cursor = JournalCursor.Encode(e) }).ToList();
    }
}
