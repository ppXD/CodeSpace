using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Tasks.Timeline;
using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Default <see cref="IJournalWalk"/> — a thin composition (Rule 16): project the merged timeline, describe each event
/// through the registry, ENRICH it with the facts gathered off the durable records, stamp its stable cursor. The three
/// concerns stay separated: ORDERING lives upstream in the timeline projector (one merge discipline, so the journal and
/// the Activity tab can't disagree on order); CLASSIFICATION lives in the pure describers; ENRICHMENT (rationale, agent
/// cards, diffstat) lives in the <see cref="IJournalFactsSource"/>s the gatherer merges — the reads a pure describer
/// can't do. This service only threads them + stamps the opaque <see cref="JournalStep.Cursor"/> from each event's own
/// sort key — so a new source / describer / facts-source changes the journal with ZERO edit here.
/// </summary>
public sealed class JournalWalk : IJournalWalk, IScopedDependency
{
    private readonly IRunTimelineProjector _timeline;
    private readonly IJournalStepDescriberRegistry _registry;
    private readonly IJournalFactsGatherer _facts;

    public JournalWalk(IRunTimelineProjector timeline, IJournalStepDescriberRegistry registry, IJournalFactsGatherer facts)
    {
        _timeline = timeline;
        _registry = registry;
        _facts = facts;
    }

    public async Task<IReadOnlyList<JournalStep>?> WalkAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var events = await _timeline.ProjectAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (events is null) return null;   // a foreign / absent run — conflate to null exactly like the timeline projector

        var facts = await _facts.GatherAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        // Describe → enrich with this step's authored facts (keyed by the SAME id) → stamp the cursor from the EVENT's
        // sort key (not its walk position), so a step's cursor is stable across re-walks: an earlier event backfilling
        // mid-timeline never renumbers the steps around it. Order is the projector's (events arrive merged); no re-sort.
        return events.Select(e => Enrich(_registry.Describe(e), facts.For(e.Id)) with { Cursor = JournalCursor.Encode(e) }).ToList();
    }

    /// <summary>Fold this step's gathered facts onto the described step — null facts leave it bare (the common case). A set fact wins over the describer's (which never authors enrichment fields), so the source is the enrichment authority.</summary>
    private static JournalStep Enrich(JournalStep step, JournalStepFacts? facts) =>
        facts is null ? step : step with
        {
            Rationale = facts.Rationale ?? step.Rationale,
            Agents = facts.Agents ?? step.Agents,
            Deferred = facts.Deferred ?? step.Deferred,
            Plan = facts.Plan ?? step.Plan,
            Answer = facts.Answer ?? step.Answer,
            ModelCall = facts.ModelCall ?? step.ModelCall,
            Round = facts.Round ?? step.Round,
        };
}
